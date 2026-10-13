using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using SMBLibrary;
using SMBLibrary.Client;
using System.Collections.ObjectModel;

#if ANDROID
using static Android.Provider.CallLog;
#endif

// For Android file downloads
#if ANDROID
using Android.Content;
using Android.Provider;
using Android.OS;
#endif

namespace RF_File_Explorer;

public partial class MainPage : ContentPage
{
    // Browsing mode
    private BrowseMode _browseMode = BrowseMode.None;

    private string _currentLocalPath = "";

    // Current location being browsed
    private string _currentServer = "";
    private string _currentShare = "";
    private string _currentDirectory = "";

    // Credentials used when navigating between folders
    private string _currentUsername = "";
    private string _currentPassword = "";

    private ObservableCollection<LocationItem> Locations;

    public MainPage()
    {
        InitializeComponent();

        Locations = new ObservableCollection<LocationItem>
{
    new()
    {
        Name = "Internal Storage",
        Path = "/storage/emulated/0",
        Icon = "phone.png"
    },

    new()
    {
        Name = "SD Card",
        Path = "/storage/XXXX-XXXX",
        Icon = "sdcard.png"
    },

    new()
    {
        Name = "PC3",
        Path = @"\\pc3.ourhouse.net\share",
        IsRemote = true,
        Icon = "computer.png"
    }
};
    }

    private async void OnConnectClicked(object sender, EventArgs e)
    {
        // Read connection info from UI
        string uncPath = UncPathEntry.Text?.Trim();
        string username = UsernameEntry.Text?.Trim() ?? string.Empty;
        string password = PasswordEntry.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(uncPath))
        {
            await DisplayAlert("Error", "Please enter a valid UNC path.", "OK");
            return;
        }

        // Save credentials so they can be reused as the user navigates subdirectories
        _currentUsername = username;
        _currentPassword = password;
        _browseMode = BrowseMode.Remote;
        _currentServer = "";
        _currentShare = "";
        _currentDirectory = "";

        SetLoadingState(true);
        DirectoryCollectionView.ItemsSource = null;
        UpdateUploadButtonState();

        try
        {
            List<NetworkItem> files =
                await Task.Run(() =>
                    FetchNetworkDirectory(uncPath, username, password));

            DirectoryCollectionView.ItemsSource = files;
            UpdateUploadButtonState();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Operation Failed", ex.Message, "OK");
        }
        finally
        {
            SetLoadingState(false);
        }
    }

    private List<NetworkItem> FetchLocalDirectory(string path)
    {
        List<NetworkItem> results = new();

        try
        {
            DirectoryInfo dir = new DirectoryInfo(path);

            System.Diagnostics.Debug.WriteLine(
    $"Directory: {path}");

            System.Diagnostics.Debug.WriteLine(
                $"Folders: {dir.GetDirectories().Length}");

            System.Diagnostics.Debug.WriteLine(
                $"Files: {dir.GetFiles().Length}");

            // Parent folder
            if (dir.Parent != null)
            {
                results.Add(new NetworkItem
                {
                    Name = "..",
                    IsDirectory = true,
                    IsParentDirectory = true,
                    FullPath = dir.Parent.FullName
                });
            }

            foreach (DirectoryInfo folder in dir.GetDirectories())
            {
                results.Add(new NetworkItem
                {
                    Name = folder.Name,
                    IsDirectory = true,
                    FullPath = folder.FullName
                });
            }

            /*foreach (FileInfo file in dir.GetFiles())
            {
                results.Add(new NetworkItem
                {
                    Name = file.Name,
                    IsDirectory = false,
                    FullPath = file.FullName
                });
            }*/
            foreach (var file in dir.EnumerateFiles())
            {
                System.Diagnostics.Debug.WriteLine($"FILE FOUND: {file.FullName}");

                results.Add(new NetworkItem
                {
                    Name = file.Name,
                    IsDirectory = false,
                    FullPath = file.FullName
                });
            }
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to open local folder:\n{ex.Message}");
        }
        
        return results;
    }

    private List<NetworkItem> FetchNetworkDirectory(string uncPath, string username, string password)
    {
        try
        {
            // Clean up the path format (remove leading slashes if user typed them)
            uncPath = uncPath.TrimStart('\\', '/');
            string[] pathParts = uncPath.Split(new[] { '\\', '/' }, 2);

            string server = pathParts[0];

            string shareName = "";
            string subFolderPath = "";

        if (pathParts.Length > 1)
        {
            string[] shareParts2 = pathParts[1].Split(new[] { '\\', '/' }, 2);

            shareName = shareParts2[0];
            subFolderPath = shareParts2.Length > 1 ? shareParts2[1] : string.Empty;
        }

        //store current location for folder navigation
        _currentServer = server;
        //_currentShare = shareName;
        //_currentDirectory = subFolderPath;

        // Handle domain names if included in username (e.g., DOMAIN\user)
        string domain = string.Empty;
        if (username.Contains("\\"))
        {
            string[] userParts = username.Split('\\');
            domain = userParts[0];
            username = userParts[1];
        }

        SMB2Client client = new SMB2Client();

        // 1. Connect to the Server
        bool isConnected = false;
        try
        {
            isConnected = client.Connect(IPAddress.Parse(server), SMBTransportType.DirectTCPTransport);
        }
        catch (Exception ex)
        {
            // Fallback to DNS resolution if IP parsing fails
            try
            {
                IPAddress[] addresses = Dns.GetHostAddresses(server);
                if (addresses.Length > 0)
                {
                    isConnected = client.Connect(addresses[0], SMBTransportType.DirectTCPTransport);
                }
            }
            catch (Exception dnsEx)
            {
                throw new Exception($"Failed to connect to host '{server}'. DNS Error: {dnsEx.Message}. Connection Error: {ex.Message}");
            }
        }

        if (!isConnected)
        {
            throw new Exception($"Failed to connect to SMB server at {server}. NetBIOS/TCP handshake failed.");
        }

        // 2. Authenticate / Login
        NTStatus loginStatus;
        if (string.IsNullOrEmpty(username))
        {
            loginStatus = client.Login(string.Empty, "Guest", string.Empty);
        }
        else
        {
            loginStatus = client.Login(domain, username, password);
        }

        if (loginStatus != NTStatus.STATUS_SUCCESS)
        {
            client.Disconnect();
            throw new Exception($"Authentication failed. Status: {loginStatus}");
        }

        if (string.IsNullOrWhiteSpace(shareName))
        {
            List<NetworkItem> shares = new();

            NTStatus listStatus;
            var shareList = client.ListShares(out listStatus);

            if (listStatus != NTStatus.STATUS_SUCCESS)
            {
                client.Disconnect();
                throw new Exception($"Failed to enumerate shares. Status: {listStatus}");
            }

            List<NetworkItem> shareItems = new();

            foreach (var share in shareList)
            {
                shareItems.Add(new NetworkItem
                {
                    Name = share,
                    IsDirectory = true,
                    FullPath = share
                });
            }

            client.Disconnect();

            _currentServer = server;
            _currentShare = "";
            _currentDirectory = "";

            return shareItems;
        }

        // 3. Connect to Share
        ISMBFileStore fileStore;
        NTStatus shareStatus;

        // TreeConnect returns the fileStore object and outputs the status
        fileStore = client.TreeConnect(shareName, out shareStatus);

            if (shareStatus == NTStatus.STATUS_SUCCESS)
            {
                _currentShare = shareName;
                _currentDirectory = subFolderPath;
            }
            if (shareStatus != NTStatus.STATUS_SUCCESS)
        {
            client.Disconnect();
            throw new Exception($"Failed to connect to share '{shareName}'. Status: {shareStatus}");
        }

        // 4. List Directory Contents
        List<NetworkItem> results = new();
        object directoryHandle;
        FileStatus fileStatus;

        NTStatus openStatus = fileStore.CreateFile(
            out directoryHandle,
            out fileStatus,
            subFolderPath,
            AccessMask.GENERIC_READ,
            SMBLibrary.FileAttributes.Directory,
            ShareAccess.Read | ShareAccess.Write,
            CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_DIRECTORY_FILE,
            null
        );

        if (openStatus == NTStatus.STATUS_SUCCESS)
        {
            // Retrieve directory listing from the server
            NTStatus queryStatus = fileStore.QueryDirectory(
                out List<QueryDirectoryFileInformation> fileList, 
                directoryHandle, 
                "*", 
                FileInformationClass.FileDirectoryInformation);

            if (queryStatus == NTStatus.STATUS_SUCCESS || queryStatus == NTStatus.STATUS_NO_MORE_FILES)
            {
                foreach (QueryDirectoryFileInformation file in fileList)
                {
                    FileDirectoryInformation fileInfo = (FileDirectoryInformation)file;
                    // Remove current and parent directories (handling parent later)
                    if (fileInfo.FileName == "." || fileInfo.FileName == "..")
                    {
                        continue;
                    }

                    /*// Handles parent directory
                    if (fileInfo.FileName == "..")
                    {
                        results.Add(new NetworkItem
                        {
                            Name = "..",
                            IsDirectory = true,
                            //IsParentDirectory = true
                        });

                        continue;
                    }*/

                    // Check SMB flag if item is a folder or a file
                    bool isDirectory = (fileInfo.FileAttributes & SMBLibrary.FileAttributes.Directory) == SMBLibrary.FileAttributes.Directory;

                    // Build the full path of the working directory
                    string childPath =
                        string.IsNullOrEmpty(subFolderPath)
                            ? fileInfo.FileName
                            : $"{subFolderPath}\\{fileInfo.FileName}";

                    // Creates an object for the UI to use
                    results.Add(new NetworkItem
                    {
                        Name = fileInfo.FileName,
                        IsDirectory = isDirectory,
                        FullPath = childPath
                    });
                }
            }
            else
            {
                fileStore.CloseFile(directoryHandle);
                client.Disconnect();
                if (queryStatus == NTStatus.STATUS_ACCESS_DENIED)
                {
                    throw new Exception(
                        "Permission denied while reading directory contents.");
                }

                throw new Exception(
                    $"Failed to read directory items.\n\n" +
                    $"SMB Status: {queryStatus}");
            }

            fileStore.CloseFile(directoryHandle);
        }
        else
        {
            client.Disconnect();
            if (openStatus == NTStatus.STATUS_ACCESS_DENIED)
            {
                throw new Exception(
                    $"Access denied.\n\n" +
                    $"The account '{username}' does not have permission " +
                    $"to access '{subFolderPath}'.");
            }

            throw new Exception(
                $"Failed to open directory '{subFolderPath}'.\n\n" +
                $"SMB Status: {openStatus}");
        }

        client.Disconnect();

        // Add a virtual ".." folder when not at the share root so the user can go back to the parent directory
        if (!string.IsNullOrEmpty(subFolderPath))
        {
            results.Insert(0, new NetworkItem
            {
                Name = "..",
                IsDirectory = true,
                IsParentDirectory = true
            });
        }
        return results;
        }
        catch (Exception ex)
        {
            throw new Exception($"SMB operation failed for '{uncPath}': {ex.Message}", ex);
        }
    }

    private void SetLoadingState(bool isLoading)
    {
        LoadingIndicator.IsRunning = isLoading;
        LoadingIndicator.IsVisible = isLoading;
        ConnectButton.IsEnabled = !isLoading;
        UpdateUploadButtonState(isLoading);
    }

    private void UpdateUploadButtonState(bool isLoading = false)
    {
        UploadButton.IsEnabled =
            !isLoading &&
            _browseMode == BrowseMode.Remote &&
            !string.IsNullOrWhiteSpace(_currentServer) &&
            !string.IsNullOrWhiteSpace(_currentShare);
    }

    private async void DirectoryCollectionView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Ignore empty selections
        if (e.CurrentSelection.Count == 0)
            return;

        // Get the selected item
        NetworkItem item = (NetworkItem)e.CurrentSelection[0];

        DirectoryCollectionView.SelectedItem = null;

        if (_browseMode == BrowseMode.Local)
        {
            if (item.IsDirectory)
            {
                try
                {
                    _currentLocalPath = item.FullPath;

                    DirectoryCollectionView.ItemsSource =
                        await Task.Run(() =>
                            FetchLocalDirectory(_currentLocalPath));
                    UpdateUploadButtonState();
                }
                catch (Exception ex)
                {
                    await DisplayAlert("Navigation Error",
                        ex.Message,
                        "OK");
                }
            }

            if (!item.IsDirectory)
            {
                await Launcher.OpenAsync(
                    new OpenFileRequest
                    {
                        File = new ReadOnlyFile(item.FullPath)
                    });

                return;
            }

            return;
        }

        if (string.IsNullOrEmpty(_currentShare))
        {
            //_currentShare = item.Name;

            string unc = $"{_currentServer}\\{item.Name}";

            List<NetworkItem> files;

            try
            {
                files = await Task.Run(() =>
                    FetchNetworkDirectory(unc, _currentUsername, _currentPassword));

                _currentShare = item.Name;
            }
            catch (UnauthorizedAccessException ex)
            {
                await DisplayAlert("Access Denied", ex.Message, "OK");
                return;
            }
            catch (Exception ex)
            {
                await DisplayAlert("Operation Failed", ex.Message, "OK");
                return;
            }

            DirectoryCollectionView.ItemsSource = files;
            UpdateUploadButtonState();
            return;
        }

        // If clicked item is a file, download
        if (!item.IsDirectory)
        {
            bool download = await DisplayAlert(
                "Download File",
                $"Download {item.Name} to Downloads folder?",
                "Yes",
                "No");

            if (download)
                await DownloadFileAsync(item);

            return;
        }

        try
        {
            SetLoadingState(true);

            string targetPath;

            // Decide where to navigate to
            if (item.IsParentDirectory)
            {
                int index = _currentDirectory.LastIndexOf('\\');

                targetPath = index >= 0 ? _currentDirectory.Substring(0, index) : "";
            }
            else
            {
                targetPath = item.FullPath;
            }

            // Rebuild root share
            string unc = $"{_currentServer}\\{_currentShare}";

            // Append subfolder if necessary
            if (!string.IsNullOrEmpty(targetPath))
            {
                unc += "\\" + targetPath;
            }

            // Get new directory contents
            List<NetworkItem> files = await Task.Run(() => FetchNetworkDirectory(unc, _currentUsername, _currentPassword));

            _currentDirectory = targetPath;

            DirectoryCollectionView.ItemsSource = files;
            UpdateUploadButtonState();
        }
        catch (Exception ex)
        {
            await DisplayAlert("Navigation Error", ex.Message, "OK");
        }
        finally
        {
            SetLoadingState(false);
        }
    }

    private async void OnUploadClicked(object sender, EventArgs e)
    {
        if (_browseMode != BrowseMode.Remote || string.IsNullOrWhiteSpace(_currentShare))
        {
            await DisplayAlert("Upload File", "Open a remote shared folder before uploading.", "OK");
            return;
        }

        try
        {
            FileResult? pickedFile = await FilePicker.PickAsync(new PickOptions
            {
                PickerTitle = "Choose a file to upload"
            });

            if (pickedFile == null)
                return;

            string uploadName = pickedFile.FileName;
            List<string> existingFileNames = GetCurrentRemoteFileNames();
            bool replaceExisting = false;

            if (existingFileNames.Contains(uploadName, StringComparer.OrdinalIgnoreCase))
            {
                string choice = await DisplayActionSheet(
                    $"{uploadName} already exists",
                    "Cancel",
                    null,
                    "Replace",
                    "Create Copy");

                if (choice == "Cancel" || string.IsNullOrEmpty(choice))
                    return;

                if (choice == "Replace")
                {
                    replaceExisting = true;
                }
                else if (choice == "Create Copy")
                {
                    uploadName = GetUniqueRemoteFileName(uploadName, existingFileNames);
                }
            }

            string destination = GetCurrentRemoteDisplayPath();
            bool upload = await DisplayAlert(
                "Upload File",
                $"Copy {pickedFile.FileName} to {destination}?",
                "Upload",
                "Cancel");

            if (!upload)
                return;

            SetLoadingState(true);

            using Stream sourceStream = await pickedFile.OpenReadAsync();

            await Task.Run(() =>
                UploadFileToCurrentRemoteFolder(sourceStream, uploadName, replaceExisting));

            string unc = $"{_currentServer}\\{_currentShare}";
            if (!string.IsNullOrWhiteSpace(_currentDirectory))
            {
                unc += "\\" + _currentDirectory;
            }

            DirectoryCollectionView.ItemsSource =
                await Task.Run(() =>
                    FetchNetworkDirectory(unc, _currentUsername, _currentPassword));

            await DisplayAlert("Success", $"{uploadName} uploaded to {destination}", "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlert("Upload Failed", ex.Message, "OK");
        }
        finally
        {
            SetLoadingState(false);
        }
    }

    private List<string> GetCurrentRemoteFileNames()
    {
        if (DirectoryCollectionView.ItemsSource is not System.Collections.IEnumerable items)
            return new List<string>();

        return items
            .OfType<NetworkItem>()
            .Where(item => !item.IsDirectory)
            .Select(item => item.Name)
            .ToList();
    }

    private string GetUniqueRemoteFileName(string originalName, List<string> existingFileNames)
    {
        string fileNameWithoutExt = Path.GetFileNameWithoutExtension(originalName);
        string extension = Path.GetExtension(originalName);

        string candidate = originalName;
        int counter = 1;

        while (existingFileNames.Contains(candidate, StringComparer.OrdinalIgnoreCase))
        {
            candidate = $"{fileNameWithoutExt} ({counter}){extension}";
            counter++;
        }

        return candidate;
    }

    private string GetCurrentRemoteDisplayPath()
    {
        string destination = $@"\\{_currentServer}\{_currentShare}";

        if (!string.IsNullOrWhiteSpace(_currentDirectory))
        {
            destination += $@"\{_currentDirectory}";
        }

        return destination;
    }

    private void UploadFileToCurrentRemoteFolder(Stream sourceStream, string uploadName, bool replaceExisting)
    {
        string username = _currentUsername;
        string password = _currentPassword;
        string domain = "";

        if (username.Contains("\\"))
        {
            var parts = username.Split('\\');
            domain = parts[0];
            username = parts[1];
        }

        SMB2Client client = new SMB2Client();
        object? fileHandle = null;
        ISMBFileStore? store = null;

        try
        {
            bool connected = false;

            if (IPAddress.TryParse(_currentServer, out IPAddress? ip))
                connected = client.Connect(ip, SMBTransportType.DirectTCPTransport);
            else
                connected = client.Connect(Dns.GetHostAddresses(_currentServer)[0], SMBTransportType.DirectTCPTransport);

            if (!connected)
                throw new Exception("Failed to connect to SMB server");

            NTStatus loginStatus = string.IsNullOrEmpty(username)
                ? client.Login("", "Guest", "")
                : client.Login(domain, username, password);

            if (loginStatus != NTStatus.STATUS_SUCCESS)
                throw new Exception($"Login failed: {loginStatus}");

            NTStatus shareStatus;
            store = client.TreeConnect(_currentShare, out shareStatus);

            if (shareStatus != NTStatus.STATUS_SUCCESS)
                throw new Exception($"Share connect failed: {shareStatus}");

            string remotePath = string.IsNullOrEmpty(_currentDirectory)
                ? uploadName
                : $"{_currentDirectory}\\{uploadName}";

            FileStatus fileStatus;

            NTStatus openStatus = store.CreateFile(
                out fileHandle,
                out fileStatus,
                remotePath,
                AccessMask.GENERIC_WRITE,
                SMBLibrary.FileAttributes.Normal,
                ShareAccess.Read,
                replaceExisting ? CreateDisposition.FILE_OVERWRITE_IF : CreateDisposition.FILE_CREATE,
                CreateOptions.FILE_NON_DIRECTORY_FILE,
                null);

            if (openStatus != NTStatus.STATUS_SUCCESS)
            {
                if (openStatus == NTStatus.STATUS_OBJECT_NAME_COLLISION)
                    throw new Exception($"{uploadName} already exists on the remote folder.");

                throw new Exception($"Failed to create remote file: {openStatus}");
            }

            int writeSize = (int)Math.Min(
                store.MaxWriteSize == 0 ? 1024 * 1024 : store.MaxWriteSize,
                1024 * 1024);

            byte[] buffer = new byte[writeSize];
            long offset = 0;

            while (true)
            {
                int bytesRead = sourceStream.Read(buffer, 0, buffer.Length);

                if (bytesRead == 0)
                    break;

                byte[] chunk = bytesRead == buffer.Length
                    ? buffer
                    : buffer.Take(bytesRead).ToArray();

                NTStatus writeStatus = store.WriteFile(
                    out int bytesWritten,
                    fileHandle,
                    offset,
                    chunk);

                if (writeStatus != NTStatus.STATUS_SUCCESS)
                    throw new Exception($"SMB write failed: {writeStatus}");

                if (bytesWritten != bytesRead)
                    throw new Exception($"SMB write was incomplete. Wrote {bytesWritten} of {bytesRead} bytes.");

                offset += bytesWritten;
            }
        }
        finally
        {
            if (store != null && fileHandle != null)
            {
                store.CloseFile(fileHandle);
            }

            client.Disconnect();
        }
    }

    // Toggle between hidden and visible password
    private void OnTogglePasswordClicked(object sender, EventArgs e)
    {
        PasswordEntry.IsPassword = !PasswordEntry.IsPassword;

        TogglePasswordButton.Text =
            PasswordEntry.IsPassword ? "Show" : "Hide";
    }

    private async Task DownloadFileAsync(NetworkItem item)
    {
        if (item.IsDirectory)
            return;

        try
        {
            SetLoadingState(true);

            string username = _currentUsername;
            string password = _currentPassword;
            string domain = "";

            if (username.Contains("\\"))
            {
                var parts = username.Split('\\');
                domain = parts[0];
                username = parts[1];
            }

            SMB2Client client = new SMB2Client();

            // ----------------------------
            // CONNECT (safe IP/DNS)
            // ----------------------------
            bool connected = false;

            if (IPAddress.TryParse(_currentServer, out IPAddress ip))
                connected = client.Connect(ip, SMBTransportType.DirectTCPTransport);
            else
                connected = client.Connect(Dns.GetHostAddresses(_currentServer)[0], SMBTransportType.DirectTCPTransport);

            if (!connected)
                throw new Exception("Failed to connect to SMB server");

            // ----------------------------
            // LOGIN
            // ----------------------------
            NTStatus loginStatus = string.IsNullOrEmpty(username)
                ? client.Login("", "Guest", "")
                : client.Login(domain, username, password);

            if (loginStatus != NTStatus.STATUS_SUCCESS)
                throw new Exception($"Login failed: {loginStatus}");

            // ----------------------------
            // SHARE CONNECT
            // ----------------------------
            NTStatus shareStatus;
            ISMBFileStore store = client.TreeConnect(_currentShare, out shareStatus);

            if (shareStatus != NTStatus.STATUS_SUCCESS)
                throw new Exception($"Share connect failed: {shareStatus}");

            // ----------------------------
            // OPEN REMOTE FILE
            // ----------------------------
            string remotePath = string.IsNullOrEmpty(_currentDirectory)
                ? item.Name
                : $"{_currentDirectory}\\{item.Name}";

            object fileHandle;
            FileStatus fileStatus;

            NTStatus openStatus = store.CreateFile(
                out fileHandle,
                out fileStatus,
                remotePath,
                AccessMask.GENERIC_READ,
                SMBLibrary.FileAttributes.Normal,
                ShareAccess.Read,
                CreateDisposition.FILE_OPEN,
                CreateOptions.FILE_NON_DIRECTORY_FILE,
                null);

            if (openStatus != NTStatus.STATUS_SUCCESS)
                throw new Exception($"Failed to open file: {openStatus}");

            // ----------------------------
            // ANDROID DOWNLOADS (MediaStore)
            // ----------------------------
            string downloadName = item.Name;
#if ANDROID
            var resolver = Android.App.Application.Context.ContentResolver;

            bool fileExists = false;

            string selection = $"{MediaStore.IMediaColumns.DisplayName}=?";
            string[] selectionArgs = { item.Name };

            using (var cursor = resolver.Query(
                MediaStore.Downloads.ExternalContentUri,
                new[] { MediaStore.IMediaColumns.DisplayName },
                selection,
                selectionArgs,
                null))
            {
                fileExists = cursor != null && cursor.Count > 0;
            }

            if (fileExists)
            {
                string choice = await DisplayActionSheet(
                    $"{item.Name} already exists",
                    "Cancel",
                    null,
                    "Replace",
                    "Create Copy");

                if (choice == "Cancel" || string.IsNullOrEmpty(choice))
                {
                    store.CloseFile(fileHandle);
                    client.Disconnect();
                    return;
                }

                if (choice == "Create Copy")
                {
                    downloadName = GetUniqueFileName(item.Name);
                }
                else if (choice == "Replace")
                {
                    using (var cursor = resolver.Query(
                        MediaStore.Downloads.ExternalContentUri,
                        new[] { Android.Provider.BaseColumns.Id },
                        selection,
                        selectionArgs,
                        null))
                    {
                        if (cursor != null && cursor.MoveToFirst())
                        {
                            long id = cursor.GetLong(0);

                            var existingUri = Android.Content.ContentUris.WithAppendedId(
                                MediaStore.Downloads.ExternalContentUri,
                                id);

                            resolver.Delete(existingUri, null, null);
                        }
                    }
                }
            }
            ContentValues values = new ContentValues();
            values.Put(MediaStore.IMediaColumns.DisplayName, downloadName);
            values.Put(MediaStore.IMediaColumns.MimeType, "application/octet-stream");
            values.Put(MediaStore.IMediaColumns.RelativePath, Android.OS.Environment.DirectoryDownloads);

            var uri = resolver.Insert(MediaStore.Downloads.ExternalContentUri, values);

            using Stream outStream = resolver.OpenOutputStream(uri);
#else
        string localPath = Path.Combine(FileSystem.AppDataDirectory, "Downloads");
        Directory.CreateDirectory(localPath);
        string fullPath = Path.Combine(localPath, item.Name);
        using Stream outStream = File.Create(fullPath);
#endif

            // ----------------------------
            // STREAM DOWNLOAD
            // ----------------------------
            byte[] buffer;
            long offset = 0;

            while (true)
            {
                NTStatus readStatus = store.ReadFile(
                    out buffer,
                    fileHandle,
                    offset,
                    1024 * 1024);

                if (readStatus != NTStatus.STATUS_SUCCESS &&
                    readStatus != NTStatus.STATUS_END_OF_FILE)
                    throw new Exception($"SMB read failed: {readStatus}");

                if (buffer == null || buffer.Length == 0)
                    break;

                await outStream.WriteAsync(buffer, 0, buffer.Length);

                offset += buffer.Length;

                if (readStatus == NTStatus.STATUS_END_OF_FILE)
                    break;
            }

            // ----------------------------
            // CLEANUP
            // ----------------------------
            store.CloseFile(fileHandle);
            client.Disconnect();

            await DisplayAlert("Success", $"{downloadName} downloaded to Downloads", "OK");
        }
        catch (Exception ex)
        {
            await DisplayAlert("Download Failed", ex.Message, "OK");
        }
        finally
        {
            SetLoadingState(false);
        }
    }

#if ANDROID
    private string GetUniqueFileName(string originalName)
    {
        var resolver = Android.App.Application.Context.ContentResolver;

        string fileNameWithoutExt = Path.GetFileNameWithoutExtension(originalName);
        string extension = Path.GetExtension(originalName);

        string candidate = originalName;
        int counter = 1;

        while (true)
        {
            string[] projection =
            {
            MediaStore.IMediaColumns.DisplayName
        };

            string selection = $"{MediaStore.IMediaColumns.DisplayName}=?";
            string[] selectionArgs = { candidate };

            using var cursor = resolver.Query(
                MediaStore.Downloads.ExternalContentUri,
                projection,
                selection,
                selectionArgs,
                null);

            bool exists = cursor != null && cursor.Count > 0;

            if (!exists)
                return candidate;

            candidate = $"{fileNameWithoutExt} ({counter}){extension}";
            counter++;
        }
    }
#endif

    // Drawer menu (☰)
    private bool _menuOpen = false;

    private async void MenuClicked(object sender, EventArgs e)
    {
        if (_menuOpen)
        {
            await Drawer.TranslateTo(-280, 0, 200);
        }
        else
        {
            await Drawer.TranslateTo(0, 0, 200);
        }

        _menuOpen = !_menuOpen;
    }

    private void CheckAndRequestAllFilesPermission()
    {
#if ANDROID
        if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.R)
        {
            if (!Android.OS.Environment.IsExternalStorageManager)
            {
                Intent intent = new Intent(Android.Provider.Settings.ActionManageAppAllFilesAccessPermission);
                Android.Net.Uri uri = Android.Net.Uri.FromParts("package", Android.App.Application.Context.PackageName, null);
                intent.SetData(uri);
                intent.AddFlags(ActivityFlags.NewTask);
                Android.App.Application.Context.StartActivity(intent);
            }
        }
#endif
    }

    private async void InternalStorageClicked(object sender, EventArgs e)
    {
        CheckAndRequestAllFilesPermission();

        try
        {
            _browseMode = BrowseMode.Local;

            _currentLocalPath = "/storage/emulated/0";

            DirectoryCollectionView.ItemsSource =
                await Task.Run(() =>
                    FetchLocalDirectory(_currentLocalPath));
        }
        catch (Exception ex)
        {
            await DisplayAlert("Error", ex.Message, "OK");
        }
    }

    private async void SdCardClicked(object sender, EventArgs e)
    {
        await DisplayAlert("Location", "SD Card clicked", "OK");
    }

    private async void Pc3Clicked(object sender, EventArgs e)
    {
        await DisplayAlert("Location", "PC3 clicked", "OK");
    }

    private async void AddLocationClicked(object sender, EventArgs e)
    {
        await DisplayAlert("Add", "Add Location clicked", "OK");
    }
}
