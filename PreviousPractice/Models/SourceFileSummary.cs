using System.ComponentModel;

namespace PreviousPractice.Models;

public class SourceFileSummary : INotifyPropertyChanged
{
    private bool canDelete;

    public string SourceFileName { get; init; } = "manual";
    public int QuestionCount { get; init; }

    public string DisplayText => $"{SourceFileName} ({QuestionCount}개)";

    public bool CanDelete
    {
        get => canDelete;
        set
        {
            if (canDelete == value)
            {
                return;
            }

            canDelete = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanDelete)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

