using Common;
using DolphinTool.Core.Models;
using DolphinTool.Core.Rvz;
using DolphinTool.Core.Services.GameCube;
using System.Runtime.InteropServices;

namespace DolphinTool.Core.Services;

public class DolphinService
{
    private const string DllName = "dolphintool.dll";

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate bool ProgressCallbackDelegate([MarshalAs(UnmanagedType.LPStr)] string text, float percent);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void LogCallbackDelegate([MarshalAs(UnmanagedType.LPStr)] string message);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int rvz_convert_to_rvz([MarshalAs(UnmanagedType.LPUTF8Str)] string input, [MarshalAs(UnmanagedType.LPUTF8Str)] string output, [MarshalAs(UnmanagedType.LPUTF8Str)] string compression, int compressionLevel, int blockSize, ProgressCallbackDelegate? progress, LogCallbackDelegate? log);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int rvz_convert_to_iso([MarshalAs(UnmanagedType.LPUTF8Str)] string input, [MarshalAs(UnmanagedType.LPUTF8Str)] string output, [MarshalAs(UnmanagedType.LPUTF8Str)] string format, ProgressCallbackDelegate? progress, LogCallbackDelegate? log);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int rvz_convert_to_gcz([MarshalAs(UnmanagedType.LPUTF8Str)] string input, [MarshalAs(UnmanagedType.LPUTF8Str)] string output, int blockSize, ProgressCallbackDelegate? progress, LogCallbackDelegate? log);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void rvz_cancel();

    public event EventHandler<(string Message, LogLevel Level)>? LogMessage;
    public event EventHandler<ProgressEventArgs>? ProgressChanged;

    public Task ConvertFileAsync(string inputPath, string format, string outputExtension, int compressionLevel = 18, string? outputDir = null, CancellationToken CancellationToken = default)
    {
        format = format.ToLowerInvariant();

        return Task.Run(() =>
        {
            string workType = format switch
            {
                "wii" or "gcm" => "압축",
                "gcz" or "wbfs" or "wia" => "재압축",
                "rvz" => "해제",
                _ => "미지원"
            };

            if (workType == "미지원")
            {
                LogMessage?.Invoke(this, ($"지원하지 않는 포맷은 건너뜁니다: {Path.GetFileName(inputPath)}", LogLevel.Error));
                return;
            }

            var dir = outputDir ?? Path.GetDirectoryName(inputPath)!;
            var name = Path.GetFileNameWithoutExtension(inputPath);
            string outputPath = Path.Combine(dir, $"{name}.{outputExtension}");

            outputPath = Utils.GetUniqueFilePath(outputPath);

            ProgressCallbackDelegate progressCb = (text, pCancellationToken) =>
            {
                ProgressChanged?.Invoke(this, new ProgressEventArgs((int)(pCancellationToken * 100)));
                return !CancellationToken.IsCancellationRequested;
            };

            LogCallbackDelegate logCb = msg => LogMessage?.Invoke(this, (msg, LogLevel.Info));

            using var reg = CancellationToken.Register(() => rvz_cancel());

            LogMessage?.Invoke(this, ( $"{Path.GetFileName(inputPath)} {workType} 시작", LogLevel.Highlight ));

            int result;

            try
            {
                result = format switch
                {
                    "wii" or "wbfs" or "wia" =>
                        rvz_convert_to_rvz(inputPath, outputPath, "zstd", compressionLevel, 131072, progressCb, logCb),

                    "gcm" =>
                        ConvertGCancellationTokenoRvz(inputPath, outputPath, compressionLevel, CancellationToken),

                    "gcz" when DiscImageInspector.Detect(inputPath) == DiscPlatform.GameCube =>                    
                        ConvertGCancellationTokenoRvz(inputPath, outputPath, compressionLevel, CancellationToken),                   

                    "rvz" =>
                        ConvertRvzToIso(inputPath, outputPath, CancellationToken),

                    _ => -2
                };
            }
            finally
            {
                GC.KeepAlive(progressCb);
                GC.KeepAlive(logCb);
            }

            if (result == -1 || CancellationToken.IsCancellationRequested)
            {
                LogMessage?.Invoke(this, ( $"{workType} 취소됨: {Path.GetFileName(inputPath)}", LogLevel.Error ));
                throw new OperationCanceledException(CancellationToken);
            }

            if (result != 0)
            {
                if (File.Exists(outputPath))
                    try { File.Delete(outputPath); } catch { }

                LogMessage?.Invoke(this, ($"{workType} 실패 (에러 코드: {result})", LogLevel.Error));

                throw new InvalidOperationException($"{workType} 실패 (에러 코드: {result})");
            }

            if (workType == "재압축" || workType == "압축")
            {
                long originalSize = new FileInfo(inputPath).Length;
                long compressedSize = new FileInfo(outputPath).Length;

                LogMessage?.Invoke(this, ( $"압축률: {Utils.FormatFileSize(originalSize)} → {Utils.FormatFileSize(compressedSize)} ({compressedSize * 100.0 / originalSize:F1}%)", LogLevel.Highlight ));
            }
            
            LogMessage?.Invoke(this, ( $"{workType} 완료: {outputPath}", LogLevel.Ok ));
        }, CancellationToken);
    }

    private int ConvertToGcz(string inputPath, string outputPath, CancellationToken CancellationToken)
    {
        try
        {
            Action<string, string, int, Action<double>?, CancellationToken> convert = inputPath.EndsWith(".rvz", StringComparison.OrdinalIgnoreCase)
                ? RvzToGczConverter.Convert
                : IsoToGczConverter.Convert;

            convert(inputPath, outputPath, GczWriter.DefaultBlockSize,
                p => ProgressChanged?.Invoke(this, new ProgressEventArgs((int)(p * 100))), CancellationToken);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return -1;
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke(this, (ex.Message, LogLevel.Error));
            return -3;
        }
    }

    private int ConvertGCancellationTokenoRvz(string inputPath, string outputPath, int compressionLevel, CancellationToken CancellationToken)
    {
        try
        {
            IsoToRvzConverter.Convert(inputPath, outputPath, compressionLevel, 131072, p => ProgressChanged?.Invoke(this, new ProgressEventArgs((int)(p * 100))), CancellationToken);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return -1;
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke(this, (ex.Message, LogLevel.Error));
            return -3;
        }
    }

    private int ConvertRvzToIso(string inputPath, string outputPath, CancellationToken CancellationToken)
    {
        try
        {
            RvzToIsoConverter.Convert(inputPath, outputPath, p => ProgressChanged?.Invoke(this, new ProgressEventArgs((int)(p * 100))), CancellationToken);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return -1;
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke(this, (ex.Message, LogLevel.Error));
            return -3;
        }
    }
}