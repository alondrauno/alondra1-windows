namespace Alondra1.Persistence;

/// <summary>
/// The Mark of the Web: a Zone.Identifier alternate data stream saying a file came from the internet, so Windows
/// (SmartScreen, Office Protected View, script hosts) treats it as untrusted. Used for task attachments downloaded
/// from cloud storage (Dev-only spec WIN-01).
/// </summary>
public static class MarkOfTheWeb
{
    private const string Content = "[ZoneTransfer]\r\nZoneId=3\r\n";

    /// <summary>Writes the mark and reads it back. False when the file system cannot keep it (FAT, some network shares).</summary>
    public static bool Apply(string path)
    {
        try
        {
            File.WriteAllText(path + ":Zone.Identifier", Content);
            return IsMarked(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    public static bool IsMarked(string path)
    {
        try
        {
            return File.ReadAllText(path + ":Zone.Identifier").Contains("ZoneId=3", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }
}
