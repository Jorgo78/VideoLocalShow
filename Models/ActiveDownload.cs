using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace VideoLocalShow.Models;

/// <summary>
/// Tracks one in-progress download. Multiple of these can exist at once - starting a new
/// download no longer waits for a previous one to finish.
/// </summary>
public class ActiveDownload : INotifyPropertyChanged
{
    private double _progress;
    private string _statusText = string.Empty;

    public required string Label { get; init; }

    public required ICommand CancelCommand { get; init; }

    public double Progress
    {
        get => _progress;
        set
        {
            if (_progress == value)
            {
                return;
            }

            _progress = value;
            OnPropertyChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        set
        {
            if (_statusText == value)
            {
                return;
            }

            _statusText = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
