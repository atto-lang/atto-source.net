namespace Ores.Net.Compiler;

// Private staging within a trusted output parent. Not a hostile-directory
// sandbox or a directory-fsync durability guarantee. Never follow an existing
// final-path symlink/reparse point. File.Move replaces the directory entry,
// not the contents of a hardlinked prior artifact.
internal static class CilArtifactPublisher
{
    private static void CheckDestination(string fullPath)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(fullPath); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("E_ARTIFACT_PUBLICATION: output must not be a directory or reparse point");
    }

    internal static void Publish(string outputFile, byte[] bytes)
    {
        string full = Path.GetFullPath(outputFile);
        CheckDestination(full);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool owned = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew,
                FileAccess.Write, FileShare.None))
            {
                owned = true;
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            CheckDestination(full);
            File.Move(temporary, full, overwrite: true);
            owned = false;
        }
        finally
        {
            if (owned) File.Delete(temporary);
        }
    }
}
