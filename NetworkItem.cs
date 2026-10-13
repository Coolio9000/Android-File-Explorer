namespace RF_File_Explorer;

public class NetworkItem
{
    public string Name { get; set; } = "";
    public bool IsDirectory { get; set; }
    public bool IsParentDirectory { /*get { return DisplayName == ".."; }*/ get; set; }
    public string FullPath { get; set; } = "";

    public string DisplayName
    {
        get
        {
            if (IsParentDirectory)
                return "📁 ..";

            return IsDirectory ? $"📁 {Name}" : $"📄 {Name}";
        }
    }
}