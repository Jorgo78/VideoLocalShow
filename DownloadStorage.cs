namespace VideoLocalShow;

public static class DownloadStorage
{
    public static string GetFolder()
    {
        var folder = Path.Combine(FileSystem.AppDataDirectory, "Downloads");
        Directory.CreateDirectory(folder);
        return folder;
    }

    public static string GetUniqueFilePath(string fileName)
    {
        var folder = GetFolder();
        var extension = Path.GetExtension(fileName);
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);

        var candidatePath = Path.Combine(folder, fileName);
        var counter = 1;

        while (File.Exists(candidatePath))
        {
            counter++;
            candidatePath = Path.Combine(folder, $"{nameWithoutExtension} ({counter}){extension}");
        }

        return candidatePath;
    }
}
