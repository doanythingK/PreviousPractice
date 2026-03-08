namespace PreviousPractice;

public partial class SourceFileDiagnosticsPage : ContentPage
{
    public SourceFileDiagnosticsPage(string sourceFileTitle, string diagnosticsText)
    {
        InitializeComponent();
        Title = $"{sourceFileTitle} 진단";
        DiagnosticsEditor.Text = diagnosticsText;
    }
}
