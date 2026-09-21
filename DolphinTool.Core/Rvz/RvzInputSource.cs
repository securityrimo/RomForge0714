using DolphinTool.Core.Services.GameCube;

namespace DolphinTool.Core.Rvz;

internal static class RvzInputSource
{
    public static IRvzInputSource Open(string path)
    {
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.SequentialScan);

        try
        {
            if (GczSource.IsGcz(handle))
                return new GczSource(handle);

            return new PlainFileSource(handle);
        }
        catch
        {
            handle.Dispose();

            throw;
        }
    }
}