using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace VideoLocalShow.Models;

/// <summary>
/// A collapsible group heading in the downloads list. The list is flat: a video belongs to the
/// nearest heading above it (videos above the first heading are in no group), so dragging a video
/// to a spot under a heading is what moves it into that group.
/// </summary>
public class GroupHeader : INotifyPropertyChanged
{
    private string _name;
    private bool _isExpanded;
    private int _count;

    public GroupHeader(string id, string name, bool isExpanded)
    {
        Id = id;
        _name = name;
        _isExpanded = isExpanded;
    }

    public string Id { get; }

    public ICommand? ToggleCommand { get; set; }

    public ICommand? MenuCommand { get; set; }

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value)
            {
                return;
            }

            _name = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Title));
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Arrow));
        }
    }

    public int Count
    {
        get => _count;
        set
        {
            if (_count == value)
            {
                return;
            }

            _count = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Title));
        }
    }

    public string Arrow => IsExpanded ? "▾" : "▸";

    public string Title => $"{Name} ({Count})";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
