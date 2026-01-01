namespace Alias.Lib.Pdb;

/// <summary>
/// Handles PDB file operations for assembly modifications.
/// </summary>
public static class PdbHandler
{
    /// <summary>
    /// Copies an external PDB file alongside the target assembly.
    /// </summary>
    public static void CopyExternalPdb(string sourceDll, string targetDll)
    {
        var sourcePdb = Path.ChangeExtension(sourceDll, ".pdb");
        var targetPdb = Path.ChangeExtension(targetDll, ".pdb");

        // Skip if source and target are the same file
        if (string.Equals(Path.GetFullPath(sourcePdb), Path.GetFullPath(targetPdb), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (File.Exists(sourcePdb))
        {
            // Copy the PDB file - no modifications needed since we don't change method tokens
            File.Copy(sourcePdb, targetPdb, overwrite: true);
        }
    }
}
