using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AgenticRouter.Api.Contracts;

namespace AgenticRouter.Api.Execution;

public interface IFolderPickerService
{
  Task<FolderPickerResult> PickAsync(
    string? initialPath,
    CancellationToken cancellationToken
  );
}

public sealed class WindowsFolderPickerService : IFolderPickerService
{
  private const int CancelledHResult = unchecked(
    (int)0x800704C7
  );

  public async Task<FolderPickerResult> PickAsync(
    string? initialPath,
    CancellationToken cancellationToken
  )
  {
    if (!OperatingSystem.IsWindows())
    {
      return new FolderPickerResult(
        false,
        false,
        null,
        "The native folder picker is available only on Windows."
      );
    }

    return await PickWindowsAsync(
      initialPath,
      cancellationToken
    );
  }

  [SupportedOSPlatform("windows")]
  private static async Task<FolderPickerResult> PickWindowsAsync(
    string? initialPath,
    CancellationToken cancellationToken
  )
  {
    var owner = GetForegroundWindow();
    var completion = new TaskCompletionSource<FolderPickerResult>(
      TaskCreationOptions.RunContinuationsAsynchronously
    );
    var thread = new Thread(
      () => completion.TrySetResult(
        ShowPicker(
          initialPath,
          owner
        )
      )
    )
    {
      IsBackground = true,
      Name = "Agentic Router folder picker"
    };
    thread.SetApartmentState(
      ApartmentState.STA
    );
    thread.Start();

    return await completion.Task.WaitAsync(
      cancellationToken
    );
  }

  [SupportedOSPlatform("windows")]
  private static FolderPickerResult ShowPicker(
    string? initialPath,
    nint owner
  )
  {
    IFileDialog? dialog = null;
    IShellItem? initialFolder = null;
    IShellItem? result = null;

    try
    {
      dialog = (IFileDialog)(object)new FileOpenDialog();
      dialog.GetOptions(
        out var currentOptions
      );
      dialog.SetOptions(
        currentOptions
          | FileOpenOptions.PickFolders
          | FileOpenOptions.ForceFileSystem
          | FileOpenOptions.PathMustExist
          | FileOpenOptions.NoChangeDirectory
          | FileOpenOptions.DontAddToRecent
      );
      dialog.SetTitle(
        "Select trusted workspace"
      );
      dialog.SetOkButtonLabel(
        "Selecionar pasta"
      );

      if (
        !string.IsNullOrWhiteSpace(
          initialPath
        )
        && Directory.Exists(
          initialPath
        )
      )
      {
        var interfaceId = typeof(IShellItem).GUID;
        var createResult = SHCreateItemFromParsingName(
          initialPath,
          nint.Zero,
          in interfaceId,
          out initialFolder
        );

        if (createResult >= 0)
        {
          dialog.SetFolder(
            initialFolder
          );
        }
      }

      var showResult = dialog.Show(
        owner
      );

      if (showResult == CancelledHResult)
      {
        return new FolderPickerResult(
          false,
          true,
          null,
          null
        );
      }

      Marshal.ThrowExceptionForHR(
        showResult
      );
      dialog.GetResult(
        out result
      );
      result.GetDisplayName(
        ShellItemDisplayName.FileSystemPath,
        out var path
      );

      return new FolderPickerResult(
        true,
        false,
        path,
        null
      );
    }
    catch (Exception exception)
    {
      return new FolderPickerResult(
        false,
        false,
        null,
        $"The folder picker could not be opened: {exception.Message}"
      );
    }
    finally
    {
      ReleaseComObject(
        result
      );
      ReleaseComObject(
        initialFolder
      );
      ReleaseComObject(
        dialog
      );
    }
  }

  [SupportedOSPlatform("windows")]
  private static void ReleaseComObject(
    object? value
  )
  {
    if (
      value is not null
      && Marshal.IsComObject(
        value
      )
    )
    {
      Marshal.FinalReleaseComObject(
        value
      );
    }
  }

  [DllImport(
    "shell32.dll",
    CharSet = CharSet.Unicode,
    PreserveSig = true
  )]
  private static extern int SHCreateItemFromParsingName(
    string path,
    nint bindingContext,
    in Guid shellItemInterfaceId,
    [MarshalAs(UnmanagedType.Interface)] out IShellItem shellItem
  );

  [DllImport("user32.dll")]
  private static extern nint GetForegroundWindow();

  [ComImport]
  [Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
  private sealed class FileOpenDialog
  {
  }

  [ComImport]
  [Guid("42F85136-DB7E-439C-85F1-E4075D135FC8")]
  [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  private interface IFileDialog
  {
    [PreserveSig]
    int Show(
      nint owner
    );

    void SetFileTypes(
      uint count,
      nint filterSpecifications
    );

    void SetFileTypeIndex(
      uint fileType
    );

    void GetFileTypeIndex(
      out uint fileType
    );

    void Advise(
      nint events,
      out uint cookie
    );

    void Unadvise(
      uint cookie
    );

    void SetOptions(
      FileOpenOptions options
    );

    void GetOptions(
      out FileOpenOptions options
    );

    void SetDefaultFolder(
      IShellItem shellItem
    );

    void SetFolder(
      IShellItem shellItem
    );

    void GetFolder(
      out IShellItem shellItem
    );

    void GetCurrentSelection(
      out IShellItem shellItem
    );

    void SetFileName(
      [MarshalAs(UnmanagedType.LPWStr)] string name
    );

    void GetFileName(
      [MarshalAs(UnmanagedType.LPWStr)] out string name
    );

    void SetTitle(
      [MarshalAs(UnmanagedType.LPWStr)] string title
    );

    void SetOkButtonLabel(
      [MarshalAs(UnmanagedType.LPWStr)] string text
    );

    void SetFileNameLabel(
      [MarshalAs(UnmanagedType.LPWStr)] string label
    );

    void GetResult(
      out IShellItem shellItem
    );

    void AddPlace(
      IShellItem shellItem,
      int alignment
    );

    void SetDefaultExtension(
      [MarshalAs(UnmanagedType.LPWStr)] string extension
    );

    void Close(
      int hResult
    );

    void SetClientGuid(
      [In] Guid clientGuid
    );

    void ClearClientData();

    void SetFilter(
      nint filter
    );
  }

  [ComImport]
  [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
  [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  private interface IShellItem
  {
    void BindToHandler(
      nint bindingContext,
      [In] Guid handlerId,
      [In] Guid interfaceId,
      out nint result
    );

    void GetParent(
      out IShellItem parent
    );

    void GetDisplayName(
      ShellItemDisplayName displayName,
      [MarshalAs(UnmanagedType.LPWStr)] out string name
    );

    void GetAttributes(
      uint mask,
      out uint attributes
    );

    void Compare(
      IShellItem shellItem,
      uint hint,
      out int order
    );
  }

  [Flags]
  private enum FileOpenOptions : uint
  {
    NoChangeDirectory = 0x00000008,
    PickFolders = 0x00000020,
    ForceFileSystem = 0x00000040,
    PathMustExist = 0x00000800,
    DontAddToRecent = 0x02000000
  }

  private enum ShellItemDisplayName : uint
  {
    FileSystemPath = 0x80058000
  }
}
