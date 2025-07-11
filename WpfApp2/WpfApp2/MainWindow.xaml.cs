using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using System.Windows.Controls;

namespace WpfApp2
{
    public partial class MainWindow : Window
    {
        private TcpListener server;
        private TcpClient client;
        private NetworkStream networkStream;
        private Dictionary<string, FileStream> activeFileTransfers;
        private Dictionary<string, long> fileSizes;
        private Dictionary<string, long> currentProgress;
        private bool isServerRunning;
        private List<TcpClient> connectedClients;
        private const int BUFFER_SIZE = 8192;
        private readonly Dictionary<TcpClient, string> clientUsernames = new Dictionary<TcpClient, string>();
        private readonly Dictionary<string, string> validCredentials = new Dictionary<string, string>
        {
            { "user1", "pass1" },
            { "user2", "pass2" },
            { "user3", "pass3" }
        };

        public MainWindow()
        {
            InitializeComponent();
            InitializeDictionaries();
            connectedClients = new List<TcpClient>();
        }

        private void InitializeDictionaries()
        {
            activeFileTransfers = new Dictionary<string, FileStream>();
            fileSizes = new Dictionary<string, long>();
            currentProgress = new Dictionary<string, long>();
        }

        #region Server Code
        private async void btnStartServer_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!isServerRunning)
                {
                    int port = int.Parse(txtServerPort.Text);
                    server = new TcpListener(IPAddress.Any, port);
                    server.Start();
                    isServerRunning = true;
                    btnStartServer.Content = "Stop Server";
                    LogMessage($"Server started on port {port}");
                    _ = AcceptClientsAsync();
                }
                else
                {
                    StopServer();
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Server error: {ex.Message}");
            }
        }

        private void StopServer()
        {
            isServerRunning = false;
            foreach (var client in connectedClients.ToArray())
            {
                try { client.Close(); } catch { }
            }
            connectedClients.Clear();
            server?.Stop();
            btnStartServer.Content = "Start Server";
            LogMessage("Server stopped");
        }
        #endregion

        #region Client Code
        // 🔐 Handles login logic, connects to server, and verifies credentials
        private async void btnLogin_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Retrieve user input from UI
                string ip = txtServerIP.Text;
                int port = int.Parse(txtServerPort.Text);
                string username = txtUsername.Text.Trim();
                string password = pwdPassword.Password.Trim();

                // Check if username or password is empty
                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                {
                    MessageBox.Show("Username and password cannot be empty", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Validate credentials locally (e.g., from a dictionary of test users)
                if (!validCredentials.ContainsKey(username) || validCredentials[username] != password)
                {
                    MessageBox.Show("Invalid username or password", "Login Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Attempt to establish TCP connection to the server
                client = new TcpClient();
                LogMessage("Attempting to connect...");
                var connectTask = client.ConnectAsync(ip, port);

                // Wait up to 5 seconds for connection or timeout
                if (await Task.WhenAny(connectTask, Task.Delay(5000)) != connectTask)
                {
                    client.Close();
                    client = null;
                    LogMessage("Connection attempt timed out.");
                    MessageBox.Show("Connection timeout. Please ensure the server is running.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Connection successful, get stream and send login message
                networkStream = client.GetStream();
                await SendMessageAsync($"LOGIN|{username}|{password}");
                LogMessage($"Logged in as {username}");

                // Enable/disable relevant buttons on successful login
                btnLogin.IsEnabled = false;
                btnSendFile.IsEnabled = true;
                btnSend.IsEnabled = true;

                // Start receiving incoming messages in the background
                _ = Task.Run(StartReceiving);
            }
            catch (Exception ex)
            {
                LogMessage($"Connection error: {ex.Message}");
                if (client != null)
                {
                    client.Close();
                    client = null;
                }
                btnLogin.IsEnabled = true;
            }
        }

        // 💬 Handles sending a public or private chat message
        private async void btnSend_Click(object sender, RoutedEventArgs e)
        {
            // Ensure the client is connected
            if (client?.Connected != true)
            {
                MessageBox.Show("Not connected to server", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Ensure message is not empty
            if (string.IsNullOrWhiteSpace(txtMessage.Text))
            {
                MessageBox.Show("Message cannot be empty", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                string message = txtMessage.Text.Trim();
                string recipient = lstOnlineUsers.SelectedItem?.ToString();

                if (recipient != null)
                {
                    // Send private message
                    await SendMessageAsync($"PRIVATE|{recipient}|{message}");
                    lstChat.Items.Add($"[To {recipient}]: {message}");
                }
                else
                {
                    // Send public broadcast
                    await SendMessageAsync($"BROADCAST|{message}");
                    lstChat.Items.Add($"[Broadcast]: {message}");
                }

                // Clear message box after sending
                txtMessage.Clear();
            }
            catch (Exception ex)
            {
                LogMessage($"Send message error: {ex.Message}");
            }
        }

        // 📁 Handles file selection and initiates file sending
        private async void btnSendFile_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Ensure connection is active
                if (client?.Connected != true)
                {
                    MessageBox.Show("Not connected to server", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                // Ensure recipient is selected
                if (lstOnlineUsers.SelectedItem == null)
                {
                    MessageBox.Show("Please select a recipient", "Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Open file dialog for user to select a file
                var dialog = new OpenFileDialog();
                if (dialog.ShowDialog() == true)
                {
                    // Disable file send button during transmission
                    btnSendFile.IsEnabled = false;

                    // Start file transfer
                    await SendFileAsync(dialog.FileName, lstOnlineUsers.SelectedItem.ToString());

                    // Re-enable button after transfer
                    btnSendFile.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Send file error: {ex.Message}");
                btnSendFile.IsEnabled = true;
            }
        }

    
    #endregion

        #region File Transfer
    // 🔼 SEND: Asynchronously sends a file to a recipient in Base64-encoded chunks
    private async Task SendFileAsync(string filePath, string recipient)
        {
            try
            {
                // Extract filename and determine file size
                string fileName = Path.GetFileName(filePath);
                long fileSize = new FileInfo(filePath).Length;

                
                const int chunkSize = 1024 * 1024;
                byte[] buffer = new byte[chunkSize];

                // Notify recipient a file transfer is starting
                await SendMessageAsync($"FILE_START|{recipient}|{fileName}|{fileSize}");

                // Open the file for reading
                using (FileStream fs = File.OpenRead(filePath))
                {
                    int bytesRead;
                    long totalBytesSent = 0;

                    // Read and send the file in chunks
                    while ((bytesRead = await fs.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        // Copy only the read portion into a smaller array
                        byte[] chunk = new byte[bytesRead];
                        Array.Copy(buffer, chunk, bytesRead);

                       
                        string base64Chunk = Convert.ToBase64String(chunk);

                        // Send encoded chunk
                        await SendMessageAsync($"FILE_CHUNK|{recipient}|{fileName}|{base64Chunk}");

                        // Update progress and optionally delay to prevent flooding the network
                        totalBytesSent += bytesRead;
                        UpdateProgress(totalBytesSent, fileSize);
                        await Task.Delay(10);
                    }
                }

                // Notify recipient that the file has finished sending
                await SendMessageAsync($"FILE_END|{recipient}|{fileName}");
                LogMessage($"File sent: {fileName}");
                UpdateProgress(0, 100); // Reset progress bar
            }
            catch (Exception ex)
            {
                LogMessage($"Error sending file: {ex.Message}");
                throw;
            }
        }

        // 🔁 Updates the progress bar and text with current transfer status
        private void UpdateProgress(long current, long total)
        {
            Dispatcher.Invoke(() =>
            {
                // Calculate and display percentage of file transferred
                double percentage = total > 0 ? (double)current / total * 100 : 0;
                transferProgress.Value = percentage;
                progressText.Text = $"{percentage:F1}% ({FormatFileSize(current)} / {FormatFileSize(total)})";
            });
        }

        // 🔁 Converts a byte size to a human-readable format (B, KB, MB, GB)
        private string FormatFileSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB" };
            int order = 0;
            double size = bytes;

            // Divide by 1024 until the value is under 1024 or unit is GB
            while (size >= 1024 && order < sizes.Length - 1)
            {
                order++;
                size /= 1024;
            }

            return $"{size:0.##} {sizes[order]}";
        }

        // 🔽 RECEIVE: Handles the start of a file transfer, prepares a unique path for writing
        private void HandleFileStart(string[] parts)
        {
            // Expected message format: FILE_START|recipient|filename|filesize
            if (parts.Length != 4)
            {
                LogMessage("Invalid FILE_START message format.");
                return;
            }

            string fileName = parts[2];

            // Validate and parse file size
            if (!long.TryParse(parts[3], out long fileSize))
            {
                LogMessage("Invalid file size received.");
                return;
            }

            // Generate a safe file path on the desktop to store the file
            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string filePath = Path.Combine(desktopPath, fileName);

            // Ensure uniqueness: add suffix if file already exists
            int fileCounter = 1;
            string uniqueFilePath = filePath;
            while (File.Exists(uniqueFilePath))
            {
                uniqueFilePath = Path.Combine(desktopPath,
                    $"{Path.GetFileNameWithoutExtension(fileName)}_{fileCounter}{Path.GetExtension(fileName)}");
                fileCounter++;
            }

            try
            {
                // Create and register a new FileStream for writing incoming chunks
                FileStream fs = new FileStream(uniqueFilePath, FileMode.Create, FileAccess.Write);
                activeFileTransfers[fileName] = fs;
                fileSizes[fileName] = fileSize;
                currentProgress[fileName] = 0;

                LogMessage($"Started receiving file: {fileName} -> Saving to {uniqueFilePath} ({FormatFileSize(fileSize)})");
            }
            catch (Exception ex)
            {
                LogMessage($"Error starting file transfer for {fileName}: {ex.Message}");
            }
        }

        // 🔽 RECEIVE: Handles an incoming chunk and appends it to the open file stream
        private void HandleFileChunk(string[] parts)
        {
            // Expected format: FILE_CHUNK|recipient|filename|base64chunk
            if (parts.Length != 4)
            {
                LogMessage("Invalid FILE_CHUNK message format.");
                return;
            }

            string fileName = parts[2];
            string base64Chunk = parts[3];

            // Verify that a file stream exists for the specified file
            if (!activeFileTransfers.ContainsKey(fileName))
            {
                LogMessage($"No active file transfer for {fileName}.");
                return;
            }

            try
            {
                // Convert base64 string back to byte array
                FileStream fs = activeFileTransfers[fileName];
                byte[] chunk = Convert.FromBase64String(base64Chunk);

                // Write chunk to file and update progress
                fs.Write(chunk, 0, chunk.Length);
                currentProgress[fileName] += chunk.Length;
                UpdateProgress(currentProgress[fileName], fileSizes[fileName]);
            }
            catch (Exception ex)
            {
                LogMessage($"Error writing file chunk for {fileName}: {ex.Message}");
            }
        }

        // 🔽 RECEIVE: Finalizes file reception, closes stream, shows UI message
        private void HandleFileEnd(string[] parts)
        {
            // Expected format: FILE_END|recipient|filename
            if (parts.Length != 3)
            {
                LogMessage("Invalid FILE_END message format.");
                return;
            }

            string fileName = parts[2];

            // Confirm an active transfer exists for this file
            if (!activeFileTransfers.ContainsKey(fileName))
            {
                LogMessage($"No active file transfer to complete for {fileName}.");
                return;
            }

            try
            {
                // Finalize file writing and clean up tracking dictionaries
                FileStream fs = activeFileTransfers[fileName];
                string savedFilePath = fs.Name;
                fs.Flush();
                fs.Close();

                activeFileTransfers.Remove(fileName);
                fileSizes.Remove(fileName);
                currentProgress.Remove(fileName);

                LogMessage($"File transfer completed: {fileName} saved to {savedFilePath}");

                // Notify user with a message box on the UI thread
                Dispatcher.Invoke(() =>
                {
                    MessageBox.Show($"File '{fileName}' has been downloaded successfully!\nSaved to: {savedFilePath}",
                                    "Download Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                    UpdateProgress(0, 100); // Reset UI progress bar
                });
            }
            catch (Exception ex)
            {
                LogMessage($"Error ending file transfer for {fileName}: {ex.Message}");
                Dispatcher.Invoke(() =>
                {
                    MessageBox.Show($"Error saving file '{fileName}': {ex.Message}", "Download Error",
                                    MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
        }

        #endregion

        #region Network Operations
        private async Task SendMessageAsync(string message)
        {
            if (networkStream == null) return;
            byte[] messageBytes = Encoding.UTF8.GetBytes(message + "\n");
            await networkStream.WriteAsync(messageBytes, 0, messageBytes.Length);
            await networkStream.FlushAsync();
        }

        private async Task AcceptClientsAsync()
        {
            while (isServerRunning)
            {
                try
                {
                    TcpClient newClient = await server.AcceptTcpClientAsync();
                    connectedClients.Add(newClient);
                    _ = HandleClientAsync(newClient);
                }
                catch (Exception) when (!isServerRunning) { }
                catch (Exception ex)
                {
                    LogMessage($"Error accepting client: {ex.Message}");
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            byte[] buffer = new byte[BUFFER_SIZE];
            StringBuilder messageBuilder = new StringBuilder();

            try
            {
                using (NetworkStream stream = client.GetStream())
                {
                    while (isServerRunning)
                    {
                        int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                        if (bytesRead == 0) break;

                        string chunk = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        messageBuilder.Append(chunk);

                        while (messageBuilder.ToString().Contains("\n"))
                        {
                            int newlineIndex = messageBuilder.ToString().IndexOf("\n");
                            string message = messageBuilder.ToString(0, newlineIndex);
                            messageBuilder.Remove(0, newlineIndex + 1);
                            await ProcessServerMessageAsync(message, client, stream);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Client handler error: {ex.Message}");
            }
            finally
            {
                connectedClients.Remove(client);
                clientUsernames.Remove(client);
                client.Close();
                await BroadcastUserListAsync();
            }
        }

        private async Task StartReceiving()
        {
            byte[] buffer = new byte[BUFFER_SIZE];
            StringBuilder messageBuilder = new StringBuilder();

            try
            {
                while (client?.Connected == true)
                {
                    int bytesRead = await networkStream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;

                    string chunk = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    messageBuilder.Append(chunk);

                    while (messageBuilder.ToString().Contains("\n"))
                    {
                        int newlineIndex = messageBuilder.ToString().IndexOf("\n");
                        string message = messageBuilder.ToString(0, newlineIndex);
                        messageBuilder.Remove(0, newlineIndex + 1);
                        ProcessMessage(message, client);
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage($"Receive error: {ex.Message}");
            }
            finally
            {
                Dispatcher.Invoke(() =>
                {
                    LogMessage("Disconnected from server");
                    btnLogin.IsEnabled = true;
                    btnSendFile.IsEnabled = false;
                    btnSend.IsEnabled = false;
                });
            }
        }
        #endregion

        #region Message Processing
        /// <summary>
        /// Server-side handler: Processes commands received from a connected client.
        /// Handles login, messaging (private/broadcast), and file transfer routing.
        /// </summary>
        private async Task ProcessServerMessageAsync(string message, TcpClient sender, NetworkStream stream)
        {
            string[] parts = message.Split('|');
            if (parts.Length == 0) return;

            string command = parts[0];

            switch (command)
            {
                // 🔐 LOGIN handling
                case "LOGIN":
                    if (parts.Length > 2)
                    {
                        string username = parts[1];
                        string password = parts[2];

                        // Check credentials validity
                        if (validCredentials.ContainsKey(username) && validCredentials[username] == password)
                        {
                            // Prevent duplicate logins
                            if (clientUsernames.ContainsValue(username))
                            {
                                byte[] errorMsg = Encoding.UTF8.GetBytes("ERROR|Username already in use\n");
                                await stream.WriteAsync(errorMsg, 0, errorMsg.Length);
                                sender.Close();
                            }
                            else
                            {
                                // Register client and update user list
                                clientUsernames[sender] = username;
                                await BroadcastUserListAsync();
                                LogMessage($"User {username} logged in");
                            }
                        }
                        else
                        {
                            byte[] errorMsg = Encoding.UTF8.GetBytes("ERROR|Invalid credentials\n");
                            await stream.WriteAsync(errorMsg, 0, errorMsg.Length);
                            sender.Close();
                        }
                    }
                    break;

                // 💬 PRIVATE message routing
                case "PRIVATE":
                    if (parts.Length > 2 && clientUsernames.ContainsKey(sender))
                    {
                        string recipient = parts[1];
                        string msg = parts[2];

                        // Send to the intended recipient
                        foreach (var client in clientUsernames)
                        {
                            if (client.Value == recipient)
                            {
                                byte[] msgBytes = Encoding.UTF8.GetBytes($"PRIVATE|{clientUsernames[sender]}|{msg}\n");
                                await client.Key.GetStream().WriteAsync(msgBytes, 0, msgBytes.Length);
                                break;
                            }
                        }
                    }
                    break;

                // 📢 BROADCAST message routing
                case "BROADCAST":
                    if (parts.Length > 1 && clientUsernames.TryGetValue(sender, out string senderUsername))
                    {
                        string msg = string.Join("|", parts, 1, parts.Length - 1);
                        string broadcastMsg = $"BROADCAST|{senderUsername}|{msg}";

                        // Send to all other connected clients
                        foreach (var c in connectedClients)
                        {
                            if (c != sender)
                            {
                                byte[] msgBytes = Encoding.UTF8.GetBytes(broadcastMsg + "\n");
                                await c.GetStream().WriteAsync(msgBytes, 0, msgBytes.Length);
                            }
                        }
                    }
                    break;

                // 📁 FILE transfer routing (start, chunk, end)
                case "FILE_START":
                case "FILE_CHUNK":
                case "FILE_END":
                    if (clientUsernames.ContainsKey(sender))
                    {
                        foreach (var c in connectedClients)
                        {
                            if (c != sender && clientUsernames.ContainsKey(c) && clientUsernames[c] == parts[1])
                            {
                                byte[] msgBytes = Encoding.UTF8.GetBytes(message + "\n");
                                await c.GetStream().WriteAsync(msgBytes, 0, msgBytes.Length);
                                break;
                            }
                        }
                    }
                    break;
            }
        }

        /// <summary>
        /// Sends updated list of online users to all connected clients.
        /// </summary>
        private async Task BroadcastUserListAsync()
        {
            string userList = string.Join(",", clientUsernames.Values);
            foreach (var c in connectedClients)
            {
                byte[] msgBytes = Encoding.UTF8.GetBytes($"USER_LIST|{userList}\n");
                await c.GetStream().WriteAsync(msgBytes, 0, msgBytes.Length);
            }
        }

        /// <summary>
        /// Client-side handler: Interprets messages received from the server.
        /// Handles errors, file transfers, chat messages, and user list updates.
        /// </summary>
        private void ProcessMessage(string message, TcpClient sender)
        {
            string[] parts = message.Split('|');
            if (parts.Length < 1) return;

            string command = parts[0];

            switch (command)
            {
                // ❌ Display server error messages
                case "ERROR":
                    if (parts.Length > 1)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            MessageBox.Show(parts[1], "Server Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            btnLogin.IsEnabled = true;
                            btnSendFile.IsEnabled = false;
                            btnSend.IsEnabled = false;
                        });
                    }
                    break;

                // 📁 File transfer: Start
                case "FILE_START":
                    if (parts.Length == 4)
                    {
                        HandleFileStart(parts);
                    }
                    else
                    {
                        LogMessage("Invalid FILE_START message format.");
                    }
                    break;

                // 📁 File transfer: Chunk
                case "FILE_CHUNK":
                    if (parts.Length == 4)
                    {
                        HandleFileChunk(parts);
                    }
                    else
                    {
                        LogMessage("Invalid FILE_CHUNK message format.");
                    }
                    break;

                // 📁 File transfer: End
                case "FILE_END":
                    if (parts.Length == 3)
                    {
                        HandleFileEnd(parts);
                    }
                    else
                    {
                        LogMessage("Invalid FILE_END message format.");
                    }
                    break;

                // 👥 User list update
                case "USER_LIST":
                    if (parts.Length >= 2)
                    {
                        UpdateUserList(parts[1].Split(','));
                    }
                    else
                    {
                        UpdateUserList(new string[0]);
                    }
                    break;

                // 📢 Display incoming broadcast message
                case "BROADCAST":
                    if (parts.Length >= 3)
                    {
                        string senderUsername = parts[1];
                        string msg = string.Join("|", parts, 2, parts.Length - 2);
                        Dispatcher.Invoke(() => lstChat.Items.Add($"[Broadcast from {senderUsername}]: {msg}"));
                    }
                    else
                    {
                        LogMessage("Received invalid broadcast message format.");
                    }
                    break;

                // 💬 Display incoming private message
                case "PRIVATE":
                    if (parts.Length >= 3)
                    {
                        string senderUsername = parts[1];
                        string msg = string.Join("|", parts, 2, parts.Length - 2);
                        Dispatcher.Invoke(() => lstChat.Items.Add($"[From {senderUsername}]: {msg}"));
                    }
                    else
                    {
                        LogMessage("Received invalid private message format.");
                    }
                    break;
            }
        }

        #endregion

        #region UI Updates
        private void LogMessage(string message)
        {
            Dispatcher.Invoke(() =>
            {
                lstServerLog.Items.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
                lstServerLog.ScrollIntoView(lstServerLog.Items[lstServerLog.Items.Count - 1]);
            });
        }

        private void UpdateUserList(string[] users)
        {
            Dispatcher.Invoke(() =>
            {
                lstOnlineUsers.Items.Clear();
                foreach (string user in users)
                {
                    if (!string.IsNullOrWhiteSpace(user))
                    {
                        lstOnlineUsers.Items.Add(user);
                    }
                }
            });
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            StopServer();
            client?.Close();
        }
        #endregion
    }
}