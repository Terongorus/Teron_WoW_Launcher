#include <windows.h>
#include <cstdint>
#include <cstdio>
#include <string>
#include <atomic>

namespace
{
    // WoW 1.12.1 (build 5875) FrameScript::Execute(__fastcall(cmd, source)).
    constexpr uintptr_t kExpectedImageBase = 0x00400000;
    constexpr uintptr_t kFrameScriptExecuteAddr = 0x00704CD0;
    constexpr uintptr_t kDefaultServerLoginAddr = 0x0046D160;
    constexpr UINT_PTR kPollTimerId = 0x54414C31; // 'TAL1'
    constexpr UINT kPollIntervalMs = 250;

    using FrameScriptExecuteFn = int(__fastcall*)(const char* command, const char* source);

    std::string g_account;
    std::string g_password;
    std::string g_luaTickScript;
    WNDPROC g_originalWndProc = nullptr;
    HWND g_gameHwnd = nullptr;
    std::atomic<bool> g_shuttingDown{ false };

    void WipeSensitiveData()
    {
        if (!g_account.empty())
        {
            SecureZeroMemory(g_account.data(), g_account.size());
            g_account.clear();
        }
        if (!g_password.empty())
        {
            SecureZeroMemory(g_password.data(), g_password.size());
            g_password.clear();
        }
        if (!g_luaTickScript.empty())
        {
            SecureZeroMemory(g_luaTickScript.data(), g_luaTickScript.size());
            g_luaTickScript.clear();
        }
    }

    bool IsExecutableAddress(uintptr_t addr)
    {
        MEMORY_BASIC_INFORMATION mbi{};
        if (VirtualQuery(reinterpret_cast<LPCVOID>(addr), &mbi, sizeof(mbi)) != sizeof(mbi))
        {
            return false;
        }
        if (mbi.State != MEM_COMMIT)
        {
            return false;
        }
        constexpr DWORD execFlags =
            PAGE_EXECUTE | PAGE_EXECUTE_READ | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY;
        return (mbi.Protect & execFlags) != 0;
    }

    bool ValidateClientBuild5875()
    {
        HMODULE exeBase = GetModuleHandleW(nullptr);
        if (reinterpret_cast<uintptr_t>(exeBase) != kExpectedImageBase)
        {
            return false;
        }

        return IsExecutableAddress(kFrameScriptExecuteAddr) &&
               IsExecutableAddress(kDefaultServerLoginAddr);
    }

    std::string EscapeLuaString(const std::string& input)
    {
        std::string out;
        out.reserve(input.size() * 2);
        for (unsigned char c : input)
        {
            switch (c)
            {
            case '\\': out += "\\\\"; break;
            case '"':  out += "\\\""; break;
            case '\'': out += "\\'"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\0': out += "\\000"; break;
            default:
                if (c < 0x20)
                {
                    char buf[8];
                    std::snprintf(buf, sizeof(buf), "\\%03u", static_cast<unsigned>(c));
                    out += buf;
                }
                else
                {
                    out.push_back(static_cast<char>(c));
                }
                break;
            }
        }
        return out;
    }

    void BuildLuaTickScript(bool rememberAccountName)
    {
        const std::string acct = EscapeLuaString(g_account);
        const std::string pass = EscapeLuaString(g_password);
        const char* saveAcctChecked = rememberAccountName ? "1" : "0";
        const std::string setRemember =
            std::string("if AccountLoginSaveAccountName then AccountLoginSaveAccountName:SetChecked(") +
            saveAcctChecked + ") end\n";

        g_luaTickScript =
            "if AccountLogin and AccountLogin:IsVisible() and DefaultServerLogin then\n"
            "  if not __TalInit then\n"
            "    __TalInit = 1\n"
            "    __TalRetries = 0\n"
            "    " + setRemember +
            "    if AccountLoginAccountEdit then AccountLoginAccountEdit:SetText(\"" + acct + "\") end\n"
            "    if AccountLoginPasswordEdit then AccountLoginPasswordEdit:SetText(\"" + pass + "\") end\n"
            "    DefaultServerLogin(\"" + acct + "\", \"" + pass + "\")\n"
            "  elseif __TalAway then\n"
            "    __TalAway = nil\n"
            "    local dlg = GlueDialog and GlueDialog:IsVisible()\n"
            "    local which = dlg and GlueDialog.which or nil\n"
            "    if (not dlg or which == \"DISCONNECTED\") and (__TalRetries or 0) < 1 then\n"
            "      if dlg and which == \"DISCONNECTED\" then GlueDialog:Hide() end\n"
            "      __TalRetries = (__TalRetries or 0) + 1\n"
            "      " + setRemember +
            "      if AccountLoginAccountEdit then AccountLoginAccountEdit:SetText(\"" + acct + "\") end\n"
            "      if AccountLoginPasswordEdit then AccountLoginPasswordEdit:SetText(\"" + pass + "\") end\n"
            "      DefaultServerLogin(\"" + acct + "\", \"" + pass + "\")\n"
            "    end\n"
            "  end\n"
            "elseif CharacterSelect and CharacterSelect:IsVisible() then\n"
            "  __TalAway = 1\n"
            "  __TalRetries = 0\n"
            "elseif not AccountLogin then\n"
            "  __TalAway = 1\n"
            "  __TalRetries = 0\n"
            "end\n";
    }

    LRESULT CALLBACK HookedWndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam)
    {
        if (msg == WM_TIMER && wParam == kPollTimerId && !g_shuttingDown.load())
        {
            static bool inTick = false;
            if (!inTick && !g_luaTickScript.empty())
            {
                inTick = true;
                auto exec = reinterpret_cast<FrameScriptExecuteFn>(kFrameScriptExecuteAddr);
                exec(g_luaTickScript.c_str(), "TeronAutoLogin");
                inTick = false;
            }
            return 0;
        }

        if (msg == WM_NCDESTROY)
        {
            KillTimer(hwnd, kPollTimerId);
            WNDPROC orig = g_originalWndProc;
            g_originalWndProc = nullptr;
            g_gameHwnd = nullptr;
            if (orig)
            {
                SetWindowLongPtrW(hwnd, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(orig));
                return CallWindowProcW(orig, hwnd, msg, wParam, lParam);
            }
        }

        return CallWindowProcW(g_originalWndProc? g_originalWndProc : DefWindowProcW, hwnd, msg, wParam, lParam);
    }

    struct EnumState
    {
        DWORD pid;
        HWND found;
    };

    BOOL CALLBACK EnumWindowsProc(HWND hwnd, LPARAM lParam)
    {
        auto* state = reinterpret_cast<EnumState*>(lParam);
        if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != nullptr)
        {
            return TRUE;
        }

        DWORD winPid = 0;
        GetWindowThreadProcessId(hwnd, &winPid);
        if (winPid == state->pid)
        {
            state->found = hwnd;
            return FALSE;
        }
        return TRUE;
    }

    HWND WaitForMainWindow(DWORD timeoutMs)
    {
        const ULONGLONG deadline = GetTickCount64() + timeoutMs;
        const DWORD pid = GetCurrentProcessId();
        while (!g_shuttingDown.load() && GetTickCount64() < deadline)
        {
            EnumState state{ pid, nullptr };
            EnumWindows(EnumWindowsProc, reinterpret_cast<LPARAM>(&state));
            if (state.found != nullptr)
            {
                return state.found;
            }
            Sleep(200);
        }
        return nullptr;
    }

    bool ReadExact(HANDLE pipe, void* buffer, DWORD bytesToRead)
    {
        auto* ptr = static_cast<uint8_t*>(buffer);
        DWORD total = 0;
        while (total < bytesToRead)
        {
            DWORD read = 0;
            if (!ReadFile(pipe, ptr + total, bytesToRead - total, &read, nullptr) || read == 0)
            {
                return false;
            }
            total += read;
        }
        return true;
    }

    bool WriteLine(HANDLE pipe, const char* line)
    {
        DWORD len = static_cast<DWORD>(std::strlen(line));
        DWORD written = 0;
        return WriteFile(pipe, line, len, &written, nullptr) && written == len;
    }

    DWORD WINAPI WorkerThread(LPVOID)
    {
        wchar_t pipeName[128];
        std::swprintf(pipeName, 128, L"\\\\.\\pipe\\TeronAutoLogin_%lu", GetCurrentProcessId());

        HANDLE pipe = INVALID_HANDLE_VALUE;
        for (int attempt = 0; attempt < 25 && !g_shuttingDown.load(); ++attempt)
        {
            pipe = CreateFileW(
                pipeName,
                GENERIC_READ | GENERIC_WRITE,
                0,
                nullptr,
                OPEN_EXISTING,
                0,
                nullptr);

            if (pipe != INVALID_HANDLE_VALUE)
            {
                break;
            }

            if (GetLastError() == ERROR_PIPE_BUSY)
            {
                WaitNamedPipeW(pipeName, 200);
            }
            else
            {
                Sleep(200);
            }
        }

        if (pipe == INVALID_HANDLE_VALUE)
        {
            return 0;
        }

        uint32_t acctLen = 0;
        uint32_t passLen = 0;
        uint8_t rememberFlag = 0;
        bool ok = ReadExact(pipe, &acctLen, sizeof(acctLen)) && acctLen > 0 && acctLen <= 256;
        if (ok)
        {
            g_account.resize(acctLen);
            ok = ReadExact(pipe, g_account.data(), acctLen);
        }
        if (ok)
        {
            ok = ReadExact(pipe, &passLen, sizeof(passLen)) && passLen <= 256;
        }
        if (ok && passLen > 0)
        {
            g_password.resize(passLen);
            ok = ReadExact(pipe, g_password.data(), passLen);
        }
        if (ok)
        {
            ok = ReadExact(pipe, &rememberFlag, sizeof(rememberFlag));
        }

        if (!ok || !ValidateClientBuild5875())
        {
            WriteLine(pipe, "HOOK_FAILED\n");
            CloseHandle(pipe);
            WipeSensitiveData();
            return 0;
        }

        BuildLuaTickScript(rememberFlag != 0);
        WriteLine(pipe, "HOOK_OK\n");
        CloseHandle(pipe);

        HWND hwnd = WaitForMainWindow(120000);
        if (hwnd != nullptr && !g_shuttingDown.load())
        {
            g_gameHwnd = hwnd;
            g_originalWndProc = reinterpret_cast<WNDPROC>(
                SetWindowLongPtrW(hwnd, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(HookedWndProc)));
            if (g_originalWndProc != nullptr)
            {
                SetTimer(hwnd, kPollTimerId, kPollIntervalMs, nullptr);
            }
        }

        return 0;
    }
}

BOOL APIENTRY DllMain(HMODULE hModule, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(hModule);
        HANDLE hThread = CreateThread(nullptr, 0, WorkerThread, nullptr, 0, nullptr);
        if (hThread != nullptr)
        {
            CloseHandle(hThread);
        }
    }
    else if (reason == DLL_PROCESS_DETACH)
    {
        g_shuttingDown.store(true);
        if (g_gameHwnd != nullptr && g_originalWndProc != nullptr)
        {
            KillTimer(g_gameHwnd, kPollTimerId);
            SetWindowLongPtrW(g_gameHwnd, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(g_originalWndProc));
            g_originalWndProc = nullptr;
            g_gameHwnd = nullptr;
        }
        WipeSensitiveData();
    }
    return TRUE;
}

