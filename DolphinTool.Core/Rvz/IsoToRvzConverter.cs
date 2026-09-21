using DolphinTool.Core.Services.GameCube;

namespace DolphinTool.Core.Rvz;

public static class IsoToRvzConverter
{
    public static void Convert(string inputPath, string outputPath, int compressionLevel = 18, int chunkSize = 131072, Action<double>? progress = null, CancellationToken ct = default)
    {
        bool succeeded = false;

        try
        {
            using var input = RvzInputSource.Open(inputPath);
            using var output = File.OpenHandle(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.None);
            var writer = new RvzGcWriter(input, output, compressionLevel, chunkSize);

            writer.Write(progress, ct);

            succeeded = true;
        }
        finally
        {
            if (!succeeded)
            {
                try
                {
                    if (File.Exists(outputPath))
                        File.Delete(outputPath);
                }
                catch { }
            }
        }
    }
}