using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace GroceryErp.Printing;

public static partial class RawPrinter
{
    [SupportedOSPlatform("windows")]
    public static void Send(string printerName, ReadOnlySpan<byte> data, string jobName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(printerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        if (data.IsEmpty) throw new ArgumentException("Print data cannot be empty.", nameof(data));
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Raw printing requires Windows.");

        if (!NativeMethods.OpenPrinter(printerName, out nint printer, 0)) ThrowLastError("open printer");
        try
        {
            var document = new DocInfo
            {
                DocumentName = jobName,
                DataType = "RAW",
            };
            nint documentPointer = Marshal.AllocHGlobal(Marshal.SizeOf<DocInfo>());
            try
            {
                Marshal.StructureToPtr(document, documentPointer, false);
                if (NativeMethods.StartDocPrinter(printer, 1, documentPointer) == 0) ThrowLastError("start print job");
                try
                {
                    if (!NativeMethods.StartPagePrinter(printer)) ThrowLastError("start printer page");
                    try
                    {
                        byte[] buffer = data.ToArray();
                        if (!NativeMethods.WritePrinter(printer, buffer, buffer.Length, out int written) || written != buffer.Length)
                            ThrowLastError("write printer data");
                    }
                    finally
                    {
                        NativeMethods.EndPagePrinter(printer);
                    }
                }
                finally
                {
                    NativeMethods.EndDocPrinter(printer);
                }
            }
            finally
            {
                Marshal.DestroyStructure<DocInfo>(documentPointer);
                Marshal.FreeHGlobal(documentPointer);
            }
        }
        finally
        {
            NativeMethods.ClosePrinter(printer);
        }
    }

    private static void ThrowLastError(string action) =>
        throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not {action}.");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string DocumentName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? OutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string DataType;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool OpenPrinter(string printerName, out nint printer, nint defaults);

        [LibraryImport("winspool.drv", EntryPoint = "ClosePrinter", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool ClosePrinter(nint printer);

        [LibraryImport("winspool.drv", EntryPoint = "StartDocPrinterW", SetLastError = true)]
        internal static partial int StartDocPrinter(nint printer, int level, nint document);

        [LibraryImport("winspool.drv", EntryPoint = "EndDocPrinter", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool EndDocPrinter(nint printer);

        [LibraryImport("winspool.drv", EntryPoint = "StartPagePrinter", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool StartPagePrinter(nint printer);

        [LibraryImport("winspool.drv", EntryPoint = "EndPagePrinter", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool EndPagePrinter(nint printer);

        [LibraryImport("winspool.drv", EntryPoint = "WritePrinter", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool WritePrinter(nint printer, byte[] bytes, int count, out int written);
    }
}
