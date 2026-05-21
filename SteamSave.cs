using Steamworks;

namespace libSteamSave;

public class SteamSave
{
    private int _gameId;
    private bool _steamInit = false;

    /// <summary>
    /// Upload a file to the save data of the given SteamID.
    /// </summary>
    /// <param name="gameId">Steam AppID of the game to use</param>
    /// <param name="toUpload">Path to File to Upload</param>
    /// <param name="destination">Remote Path to Upload File to</param>
    /// <returns>True if Success, False if Failure</returns>
    public bool UploadFile(int gameId, string toUpload, string destination)
    {
        // File Exists?
        if (!File.Exists(toUpload))
        {
            return false;
        }

        // Is it too large?
        var fileInfo = new FileInfo(toUpload);
        if (fileInfo.Length > 209715200) // 200MB Limit
        {
            Console.WriteLine($"{toUpload} is too large! 200MB limit!");
            return false;
        }

        if (AvailableSpace(gameId) < fileInfo.Length)
        {
            Console.WriteLine($"{toUpload} is too large! Not enough space available!");
            Console.WriteLine("This should be checked by the consumer of the library via AvailableSpace(gameID)!");
        }

        // Okay lets get it uploaded.
        InitSteam(gameId);
        var pendingWrite = new CallResult<RemoteStorageFileWriteAsyncComplete_t>();
        // Load File into Memory (Sadly, I don't see a way to Stream the data without loading it all into memory.
        // LMK if you know a way!
        byte[] data = File.ReadAllBytes(toUpload);
        SteamAPICall_t handle = SteamRemoteStorage.FileWriteAsync(destination, data, (uint)data.Length);
        bool callbackComplete = false;

        // If you're wondering why I did this: Steam gets upset and disconnects you if you don't do this song and dance,
        // if your file is taking longer than about a second as far as I can tell to upload.
        pendingWrite.Set(handle, (result, error) =>
        {
            callbackComplete = true;
            if (error)
            {
                Console.WriteLine($"Failed to upload {toUpload}.");
            }

            if (result.m_eResult == EResult.k_EResultOK)
            {
                Console.WriteLine($"Uploaded {toUpload} Successfully.");
            }
            else
            {
                Console.WriteLine($"Upload failed: {result.m_eResult}");
                throw new ApplicationException(result.m_eResult.ToString());
            }
        });
        while (!callbackComplete)
        {
            SteamAPI.RunCallbacks();
            Thread.Sleep(10);
        }

        return true;
    }

    /// <summary>
    /// Get Available Space in Bytes
    /// </summary>
    /// <param name="gameId">Game ID to CHeck</param>
    /// <returns>long bytes available</returns>
    public long AvailableSpace(int gameId)
    {
        InitSteam(gameId);
        bool result = SteamRemoteStorage.GetQuota(out ulong totalBytes, out ulong usedBytes);
        if (result)
        {
            return (long)(totalBytes - usedBytes);
        }
        return 0;
    }

    /// <summary>
    /// Prints Storage Information to Console in a Human Readable Format.
    /// </summary>
    /// <param name="gameId"></param>
    public void AvailableSpaceH(int gameId)
    {
        InitSteam(gameId);
        bool result = SteamRemoteStorage.GetQuota(out ulong totalBytes, out ulong usedBytes);
        if (result)
        {
            Console.WriteLine($"Total: {FormatBytes(totalBytes)}");
            Console.WriteLine($"Available: {FormatBytes(totalBytes - usedBytes)}");
            Console.WriteLine($"Used: {FormatBytes(usedBytes)}");
        }
    }

    /// <summary>
    /// Deletes a remote file
    /// </summary>
    /// <param name="gameId">Game ID to access</param>
    /// <param name="remoteFilename">The file to delete</param>
    /// <returns>true on success, false on fail.</returns>
    public bool DeleteFile(int gameId, string remoteFilename)
    {
        InitSteam(gameId);
        // Check that the file exists remotely:
        var list = FileListing(gameId);
        if (FileListing(gameId).All(x => x.Item1 != remoteFilename))
        {
            Console.WriteLine("File not Found!");
            return false;
        }

        SteamRemoteStorage.FileDelete(remoteFilename);
        return true;
    }
    
    /// <summary>
    /// Download a File from the given Game's ID
    /// </summary>
    /// <param name="gameId">GameID to access</param>
    /// <param name="remoteFilename">Remote File Name as given by FileListing</param>
    /// <param name="localFileName">Where you want it saved - Full or Partial path.</param>
    /// <returns>bool true if success, else false</returns>
    public bool DownloadFile(int gameId, string remoteFilename, string localFileName)
    {
        InitSteam(gameId);
        // Check that the file exists remotely:
        var list = FileListing(gameId);
        if (FileListing(gameId).All(x => x.Item1 != remoteFilename))
        {
            Console.WriteLine("File not Found!");
            return false;
        }
        int size = SteamRemoteStorage.GetFileSize(remoteFilename);
        if (size <= 0)
        {
            Console.WriteLine($"File not found! (TOCTOU)");
            return false;
        }
        //Check if the local directory is writable.
        if (!EnsureFileWritable(localFileName))
        {
            return false;
        }

        byte[] data = new byte[size];
        int read = SteamRemoteStorage.FileRead(remoteFilename, data, data.Length);
        if (read != data.Length)
        {
            Console.WriteLine($"Failed to read {remoteFilename}.");
            return false;
        }
        File.WriteAllBytes(localFileName, data);
        Console.WriteLine($"Wrote {remoteFilename} to {localFileName}.");
        return true;
    }
    private static bool EnsureFileWritable(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                // Create the file (and any missing directories)
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Create(path).Dispose();
            }
            else
            {
                // Check it's writable by opening it for writing
                using var fs = File.OpenWrite(path);
            }
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            Console.WriteLine($"Access denied: {path}");
            return false;
        }
        catch (IOException ex)
        {
            Console.WriteLine($"IO error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Gets the list of files available remotely.
    /// </summary>
    /// <param name="gameId">gameID to access</param>
    /// <returns>String (Filename), Int (Size in Bytes)</returns>
    public List<(string, int)> FileListing(int gameId)
    {
        InitSteam(gameId);
        int count = SteamRemoteStorage.GetFileCount();
        var listToReturn = new List<(string, int)>();
        for (int i = 0; i < count; i++)
        {
            int size;
            string filename = SteamRemoteStorage.GetFileNameAndSize(i, out size);
            listToReturn.Add((filename, size));
        }
        return listToReturn;
    }

    /// <summary>
    /// Formats a Number of Bytes as a String with appropriate suffix.
    /// </summary>
    /// <param name="bytes"></param>
    /// <returns>1024 bytes => 1KB</returns>
    private static string FormatBytes(ulong bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];

        double value = bytes;
        int suffix = 0;

        while (value >= 1024 && suffix < suffixes.Length - 1)
        {
            value /= 1024;
            suffix++;
        }

        return $"{value:F2} {suffixes[suffix]}";
    }

    /// <summary>
    /// Initializes Steam with the GameID if needed.
    /// </summary>
    /// <param name="gameId">ID of the Game in Question</param>
    /// <exception cref="ApplicationException"></exception>
    private void InitSteam(int gameId)
    {
        if (gameId == _gameId)
        {
            // Already Initialized or GameID not changed.
            return;
        }

        if (_steamInit)
        {
            SteamAPI.Shutdown();
            File.Delete("appid.txt");
        }
        File.WriteAllText("appid.txt", $"{gameId}");
        _gameId = gameId;
        if (!SteamAPI.Init())
        {
            Console.WriteLine("SteamAPI Init Failed:");
            Console.WriteLine("Please ensure Steam is Running!");
            throw new ApplicationException("Steam Init Failure");
        }
    }
}