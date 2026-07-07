using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using TeronWoWLauncher.Models;

namespace TeronWoWLauncher.Dialogs;

/// <summary>Lets the user pick which untracked AddOns folders to start tracking (adopt).</summary>
public partial class LocalAddonsDialog : Window
{
    private readonly List<(LocalAddonCandidate Candidate, CheckBox Box)> _rows = new();

    public List<LocalAddonCandidate> Selected { get; } = new();

    public LocalAddonsDialog(IReadOnlyList<LocalAddonCandidate> candidates)
    {
        InitializeComponent();

        foreach (LocalAddonCandidate candidate in candidates)
        {
            var box = new CheckBox { Content = candidate.Display, IsChecked = true };
            CandidatesPanel.Children.Add(box);
            _rows.Add((candidate, box));
        }
    }

    private void OnAdopt(object sender, RoutedEventArgs e)
    {
        Selected.Clear();
        foreach ((LocalAddonCandidate candidate, CheckBox box) in _rows)
        {
            if (box.IsChecked == true)
            {
                Selected.Add(candidate);
            }
        }

        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
