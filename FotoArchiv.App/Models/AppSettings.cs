namespace FotoArchiv.App.Models;

public sealed class AppSettings
{
    public string DestinationRoot { get; set; } = string.Empty;
    public bool SplitLocationByDate { get; set; } = true;
    public bool IncludeCountry { get; set; }
    public bool IncludeRegion { get; set; }
    public TransferMode TransferMode { get; set; } = TransferMode.Copy;
    public string QuarantineFolderName { get; set; } = "_DuplicatesReview";
}
