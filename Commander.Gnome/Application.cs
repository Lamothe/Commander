using System.Data;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Gtk;

namespace Commander.Gnome;

public class Application
{
    private readonly Adw.Application _app;
    private List<CommandInfo> _commands = [];
    private Adw.ApplicationWindow _window = null!;
    private Adw.NavigationView _navView = null!;
    private ListBox _listBox = null!;
    private readonly Dictionary<Widget, CommandInfo> _rowToCommandMap = new();
    private static string GetStoragePath()
    {
        var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "commander");
        Directory.CreateDirectory(appDataDir);
        return Path.Combine(appDataDir, "commands.json");
    }

    public Application()
    {
        _app = Adw.Application.New("com.lamothe.Commander", Gio.ApplicationFlags.FlagsNone);
        _app.OnActivate += (sender, args) => OnActivate();
    }

    public int Run()
    {
        return _app.RunWithSynchronizationContext(null);
    }

    private static async Task OnStartup()
    {
        var cssProvider = CssProvider.New();
        var cssPath = Path.Combine(AppContext.BaseDirectory, "Commander.css");
        cssProvider.LoadFromString(await global::System.IO.File.ReadAllTextAsync(cssPath));
        StyleContext.AddProviderForDisplay(
            Gdk.Display.GetDefault()!,
            cssProvider,
            800
        );
    }

    private void OnActivate()
    {
        _ = OnStartup();
        LoadCommands();

        _navView = Adw.NavigationView.New();

        var mainView = BuildMainView();
        var mainPage = Adw.NavigationPage.New(mainView, "Commander");
        UpdateCommandList();
        _navView.Add(mainPage);

        _window = Adw.ApplicationWindow.New(_app);
        _window.SetTitle("Commander");
        _window.SetDefaultSize(1000, 700);
        _window.SetContent(_navView);
        _window.Present();
    }

    private Adw.ToolbarView BuildMainView()
    {
        var toolbarView = Adw.ToolbarView.New();

        // Clean HeaderBar
        var headerBar = Adw.HeaderBar.New();
        toolbarView.AddTopBar(headerBar);

        // Setup the ListBox
        _listBox = ListBox.New();
        _listBox.AddCssClass("boxed-list");
        _listBox.SetSelectionMode(SelectionMode.None);
        _listBox.Valign = Align.Start;
        _listBox.OnRowActivated += (_, e) =>
        {
            var row = e.Row;
            if (row != null && row is Adw.ActionRow actionRow)
            {
                var commandInfo = _commands.FirstOrDefault(c => c.Name == actionRow.Title);
                if (commandInfo != null)
                {
                    ShowCommandDetailPage(commandInfo);
                }
            }
        };

        // Create the Add Button (styled to match your reference image)
        var addButton = Button.NewWithLabel("+ Add Command...");
        addButton.AddCssClass("flat"); // This removes the blue background!
        addButton.Valign = Align.Center;
        addButton.OnClicked += (_, _) => ShowCommandDetailPage(new CommandInfo("New Command", "", "", true) { IsNew = true });

        // The Magic: Use a PreferencesGroup to create the split header layout
        var prefsGroup = Adw.PreferencesGroup.New();
        prefsGroup.Title = "Commands";       // Puts the text on the left
        prefsGroup.HeaderSuffix = addButton; // Puts the flat button on the right
        prefsGroup.Add(_listBox);            // Puts your boxed list underneath

        // Wrap the group in the Clamp
        var clamp = Adw.Clamp.New();
        clamp.MaximumSize = 600;
        clamp.MarginTop = 32;
        clamp.MarginBottom = 32;
        clamp.MarginStart = 12;
        clamp.MarginEnd = 12;
        clamp.SetChild(prefsGroup);

        // ScrolledWindow
        var scrolledWindow = ScrolledWindow.New();
        scrolledWindow.HscrollbarPolicy = PolicyType.Never;
        scrolledWindow.SetChild(clamp);

        toolbarView.Content = scrolledWindow;

        return toolbarView;
    }

    private void UpdateCommandList()
    {
        var child = _listBox.GetFirstChild();
        while (child != null)
        {
            var next = child.GetNextSibling();
            _listBox.Remove(child);
            _rowToCommandMap.Remove(child);
            child = next;
        }

        if (_commands.Count == 0)
        {
            var emptyRow = Adw.ActionRow.New();
            emptyRow.Title = "No commands configured";
            emptyRow.SetSubtitle("Click \"+ Add Command\" to get started");
            emptyRow.AddCssClass("empty-state");
            _listBox.Append(emptyRow);
            return;
        }

        foreach (var commandInfo in _commands)
        {
            var row = CreateCommandRow(commandInfo);
            _listBox.Append(row);
        }
    }

    private Adw.ActionRow CreateCommandRow(CommandInfo commandInfo)
    {
        var row = Adw.ActionRow.New();
        row.Hexpand = true;
        row.Activatable = true;
        row.Title = commandInfo.Name;
        row.AddCssClass("command-row");

        // Create a circular Play/Stop button
        var playStopBtn = Button.NewFromIconName("media-playback-start-symbolic");
        playStopBtn.AddCssClass("circular");
        playStopBtn.AddCssClass("flat"); // Makes it blend into the row until hovered
        playStopBtn.Valign = Align.Center;
        playStopBtn.MarginEnd = 8; // Small gap between this button and the chevron

        playStopBtn.OnClicked += (_, _) =>
        {
            if (commandInfo.Process is { HasExited: false })
            {
                StopProcess(commandInfo);
            }
            else
            {
                // Prep the background buffer and start!
                commandInfo.OutputBuffer ??= TextBuffer.New(null);
                commandInfo.OutputBuffer.Text = "Starting process from main view...\n";
                RunCommand(commandInfo);
            }
        };

        row.AddPrefix(playStopBtn);

        // Add the Chevron
        var chevron = Image.NewFromIconName("go-next-symbolic");
        chevron.AddCssClass("dim-label");
        row.AddSuffix(chevron);

        // Update Status and Icon
        void UpdateStatus()
        {
            bool isRunning = commandInfo.Process is { HasExited: false };

            row.SetSubtitle(isRunning
                ? "Running"
                : commandInfo.Process != null
                    ? commandInfo.ExitCode.HasValue && commandInfo.ExitCode != 0
                        ? $"Failed ({commandInfo.ExitCode})"
                        : "Stopped"
                    : "Idle");

            // Swap the icon natively based on the process state
            playStopBtn.IconName = isRunning
                ? "media-playback-stop-symbolic"
                : "media-playback-start-symbolic";

            // Toggle CSS classes based on process state
            if (isRunning)
            {
                row.AddCssClass("command-running");
                row.RemoveCssClass("command-failed");
            }
            else if (commandInfo.ExitCode.HasValue && commandInfo.ExitCode != 0)
            {
                row.AddCssClass("command-failed");
                row.RemoveCssClass("command-running");
            }
            else
            {
                row.RemoveCssClass("command-running");
                row.RemoveCssClass("command-failed");
            }
        }

        UpdateStatus();

        // GTK4 Drag and Drop Controllers
        var dragSource = Gtk.DragSource.New();
        dragSource.SetActions(Gdk.DragAction.Move);
        dragSource.OnPrepare += (_, _) =>
        {
            // Package the command name into a GValue to act as the drag payload
            var val = new GObject.Value(GObject.Type.String);
            val.SetString(commandInfo.Name);
            return Gdk.ContentProvider.NewForValue(val);
        };
        row.AddController(dragSource);

        var dropTarget = Gtk.DropTarget.New(GObject.Type.String, Gdk.DragAction.Move);
        dropTarget.OnDrop += (_, args) =>
        {
            var draggedName = args.Value.GetString();
            if (!string.IsNullOrEmpty(draggedName) && draggedName != commandInfo.Name)
            {
                ReorderCommand(draggedName, commandInfo.Name);
                return true; // Indicates the drop was successfully handled
            }
            return false;
        };
        row.AddController(dropTarget);

        commandInfo.StatusChanged = () => GLib.Functions.IdleAdd(0, () =>
        {
            UpdateStatus();
            return false;
        });

        _rowToCommandMap[row] = commandInfo;

        return row;
    }

    private Adw.ToolbarView BuildDetailView(CommandInfo commandInfo)
    {
        var toolbarView = Adw.ToolbarView.New();

        var headerBar = Adw.HeaderBar.New();
        toolbarView.AddTopBar(headerBar);

        var backBtn = Button.NewWithLabel("Back");
        backBtn.AddCssClass("flat");
        backBtn.OnClicked += (_, _) => _navView.Pop();
        headerBar.PackStart(backBtn);

        var contentBox = Box.New(Orientation.Vertical, 0);
        contentBox.AddCssClass("detail-content");

        var infoGroup = Adw.PreferencesGroup.New();
        infoGroup.Title = "Command";

        var nameRow = Adw.EntryRow.New();
        nameRow.Title = "Command Name";
        nameRow.SetText(commandInfo.Name);
        nameRow.Hexpand = true;
        infoGroup.Add(nameRow);

        var executableGroup = Adw.PreferencesGroup.New();
        executableGroup.Title = "Executables";
        executableGroup.SetMarginTop(16);

        var executableRows = new List<(Adw.EntryRow Row, Button RemoveBtn)>();

        // Helper to add new rows
        void AddExecutableRow(string? initialText = null)
        {
            var row = Adw.EntryRow.New();
            row.Hexpand = true;
            row.SetText(initialText ?? string.Empty);

            // Add remove button for each row
            var removeBtn = Button.NewFromIconName("list-remove-symbolic");
            removeBtn.AddCssClass("flat");
            removeBtn.Valign = Align.Center;
            removeBtn.OnClicked += (_, _) =>
            {
                executableGroup.Remove(row);
                executableRows.RemoveAll(x => x.Row == row);
            };

            row.AddSuffix(removeBtn);
            executableGroup.Add(row);
            executableRows.Add((row, removeBtn));
        }

        // Add existing executables (if not new command)
        if (!commandInfo.IsNew && commandInfo.Executables.Count > 0)
        {
            foreach (var exec in commandInfo.Executables)
            {
                AddExecutableRow(exec);
            }
        }
        else
        {
            // Add initial empty row for new commands
            AddExecutableRow();
        }

        // Button to add another executable row
        var addExecBtn = Button.NewWithLabel("+ Add Executable");
        addExecBtn.AddCssClass("flat");
        addExecBtn.OnClicked += (_, _) => AddExecutableRow();

        executableGroup.HeaderSuffix = addExecBtn;
        infoGroup.Add(executableGroup);

        var workingDirectoryRow = Adw.EntryRow.New();
        workingDirectoryRow.Title = "Working Directory";
        workingDirectoryRow.SetText(commandInfo.WorkingDirectory ?? string.Empty);
        workingDirectoryRow.Hexpand = true;
        infoGroup.Add(workingDirectoryRow);

        contentBox.Append(infoGroup);

        if (!commandInfo.IsNew)
        {
            // Declare terminal UI first since buttons reference it
            var outputTextView = TextView.New();
            outputTextView.SetEditable(false);
            outputTextView.SetMonospace(true);
            outputTextView.AddCssClass("terminal-output");

            var terminalGroup = Adw.PreferencesGroup.New();
            terminalGroup.Title = "Terminal Output";

            var outputScroll = ScrolledWindow.New();
            outputScroll.SetVexpand(true);
            outputScroll.MinContentHeight = 350;
            outputScroll.MaxContentHeight = 500;
            outputScroll.AddCssClass("terminal-output");
            commandInfo.TerminalScroll = outputScroll;

            // Reuse existing buffer if available, otherwise create new one
            commandInfo.OutputBuffer ??= TextBuffer.New(null);
            outputTextView.Buffer = commandInfo.OutputBuffer;

            // This fires automatically whenever the text buffer changes size!
            outputScroll.Vadjustment?.OnChanged += (s, e) =>
            {
                if (commandInfo.IsAutoScrolling)
                {
                    var adj = outputScroll.Vadjustment;
                    adj.Value = adj.Upper - adj.PageSize;
                }
            };

            outputScroll.SetChild(outputTextView);

            var controlsGroup = Adw.PreferencesGroup.New();

            var controlsBox = Box.New(Orientation.Horizontal, 12);
            controlsBox.Halign = Align.End;

            Button startBtn = null!;
            Button stopBtn = null!;

            // Helper to update button states
            void UpdateButtons()
            {
                bool isRunning = commandInfo.Process is { HasExited: false };
                stopBtn.Sensitive = isRunning;
                startBtn.Sensitive = !isRunning;
            }

            bool isRunning = commandInfo.Process != null && !commandInfo.Process.HasExited;

            stopBtn = Button.NewWithLabel("Stop");
            stopBtn.AddCssClass("destructive-action");
            stopBtn.Sensitive = isRunning;

            startBtn = Button.NewWithLabel("Start");
            startBtn.AddCssClass("suggested-action");
            startBtn.Sensitive = !isRunning;
            startBtn.OnClicked += (_, _) =>
            {
                startBtn.Sensitive = false;
                stopBtn.Sensitive = true;

                // Ensure the buffer is set on the text view
                commandInfo.OutputBuffer ??= TextBuffer.New(null);
                outputTextView.Buffer = commandInfo.OutputBuffer;

                outputTextView.Buffer.Text = "Starting process...\n";
                RunCommand(commandInfo);
            };

            stopBtn.OnClicked += (_, _) =>
            {
                stopBtn.Sensitive = false;
                startBtn.Sensitive = true;
                StopProcess(commandInfo);
            };

            controlsBox.Append(startBtn);
            controlsBox.Append(stopBtn);

            controlsGroup.Add(controlsBox);
            controlsGroup.SetMarginTop(16);
            contentBox.Append(controlsGroup);

            terminalGroup.Add(outputScroll);
            contentBox.Append(terminalGroup);

            // Register button update callback
            commandInfo.ButtonsChanged = UpdateButtons;
        }

        // Initial button state
        commandInfo.ButtonsChanged?.Invoke();

        // Save/Cancel/Remove buttons
        var footerBox = Box.New(Orientation.Horizontal, 12);
        footerBox.Halign = Align.Fill;
        footerBox.Hexpand = true;

        var saveBtn = Button.NewWithLabel("Save");
        saveBtn.AddCssClass("suggested-action");
        saveBtn.OnClicked += (_, _) =>
        {
            try
            {
                var newName = nameRow.GetText()?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(newName))
                {
                    Console.Error.WriteLine("Save failed: Name is empty");
                    return;
                }

                if (_commands.Any(c => c != commandInfo && c.Name == newName))
                {
                    Console.Error.WriteLine("Save failed: Command name already exists");
                    return;
                }

                // Collect executables from the list we've been maintaining
                var allExecutables = executableRows.Select(x => x.Row.GetText()?.Trim() ?? string.Empty)
                                                   .Where(text => !string.IsNullOrEmpty(text))
                                                   .ToList();

                if (allExecutables.Count == 0)
                {
                    Console.Error.WriteLine("Save failed: No executables defined");
                    return;
                }

                commandInfo.Name = newName;
                commandInfo.Executables = allExecutables;
                commandInfo.WorkingDirectory = workingDirectoryRow.GetText()?.Trim() ?? string.Empty;

                if (commandInfo.IsNew)
                {
                    _commands.Add(commandInfo);
                    commandInfo.IsNew = false;
                }

                SaveCommands();
                UpdateCommandList();
                _navView.Pop();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Save button error: {ex}");
            }
        };

        var cancelBtn = Button.NewWithLabel("Cancel");
        cancelBtn.AddCssClass("destructive-action");
        cancelBtn.OnClicked += (_, _) => _navView.Pop();

        var spacer = Box.New(Orientation.Horizontal, 0);
        spacer.Hexpand = true;

        if (!commandInfo.IsNew)
        {
            var removeBtn = Button.NewWithLabel("Remove");
            removeBtn.AddCssClass("destructive-action");
            removeBtn.OnClicked += (_, _) =>
            {
                var dialog = new Adw.AlertDialog
                {
                    Heading = "Delete Command?",
                    Body = "This will permanently delete the command and stop any running process. This action cannot be undone."
                };
                dialog.AddResponse("cancel", "Cancel");
                dialog.AddResponse("delete", "Delete");
                dialog.SetResponseAppearance("delete", Adw.ResponseAppearance.Destructive);
                dialog.SetCloseResponse("cancel");

                dialog.OnResponse += (_, args) =>
                {
                    if (args.Response == "delete")
                    {
                        StopProcess(commandInfo);
                        _commands.Remove(commandInfo);
                        SaveCommands();
                        _navView.Pop();
                        UpdateCommandList();
                    }
                };

                dialog.Present(_window);
            };


            footerBox.Append(removeBtn);
        }
        footerBox.Append(spacer);

        footerBox.Append(cancelBtn);
        footerBox.Append(saveBtn);

        var footerGroup = Adw.PreferencesGroup.New();
        footerGroup.SetMarginTop(16);
        footerGroup.Add(footerBox);
        contentBox.Append(footerGroup);

        toolbarView.Content = contentBox;
        return toolbarView;
    }

    private void ShowCommandDetailPage(CommandInfo commandInfo)
    {
        var detailView = BuildDetailView(commandInfo);
        var detailPage = Adw.NavigationPage.New(detailView, commandInfo.Name);
        _navView.Push(detailPage);
    }

    private void ReorderCommand(string sourceName, string targetName)
    {
        var sourceCmd = _commands.FirstOrDefault(c => c.Name == sourceName);
        var targetCmd = _commands.FirstOrDefault(c => c.Name == targetName);

        if (sourceCmd == null || targetCmd == null || sourceCmd == targetCmd)
        {
            return;
        }

        // Remove the dragged item
        _commands.Remove(sourceCmd);

        // Find the new index of the target and insert the dragged item there
        int targetIndex = _commands.IndexOf(targetCmd);
        _commands.Insert(targetIndex, sourceCmd);

        // Save state and refresh the UI
        SaveCommands();
        UpdateCommandList();
    }

    private static void RunCommand(CommandInfo commandInfo)
    {
        // Ensure the buffer exists so it can capture logs in the background!
        commandInfo.OutputBuffer ??= TextBuffer.New(null);

        // Start the first executable
        ExecuteNextExecutable(commandInfo, 0);
    }

    private static void ExecuteNextExecutable(CommandInfo commandInfo, int index)
    {
        // If no more executables, we're done
        if (index >= commandInfo.Executables.Count)
        {
            commandInfo.ExitCode = 0;
            commandInfo.Process = null;
            commandInfo.StatusChanged?.Invoke();
            commandInfo.ButtonsChanged?.Invoke();
            GLib.Functions.IdleAdd(0, () =>
            {
                UpdateOutput(commandInfo, "All executables completed successfully.\n");
                return false;
            });
            return;
        }

        var executableStr = commandInfo.Executables[index];

        // Split the command and arguments safely
        var parts = executableStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            // Skip empty executables and move to next
            ExecuteNextExecutable(commandInfo, index + 1);
            return;
        }

        string exe = parts[0];

        // Smart Path Resolution
        // If a working directory is provided and the executable isn't already an absolute path
        if (!string.IsNullOrWhiteSpace(commandInfo.WorkingDirectory) && !exe.StartsWith('/'))
        {
            // Strip the explicit "./" if you typed it, so Path.Combine works cleanly
            string cleanExe = exe.StartsWith("./") ? exe.Substring(2) : exe;

            string fullPath = Path.Combine(commandInfo.WorkingDirectory, cleanExe);

            // If the file actually exists in the working directory, upgrade it to an absolute path
            if (File.Exists(fullPath))
            {
                exe = fullPath;
            }
        }

        UpdateOutput(commandInfo, $"Starting executable {index + 1}/{commandInfo.Executables.Count}: {exe}\n");

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(AppContext.BaseDirectory, "Commander.ProcessWrapper"),

                // We must use ArgumentList below to prevent spaces from breaking the command.
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = commandInfo.WorkingDirectory
            },
            EnableRaisingEvents = true
        };

        // Pass the resolved executable and its arguments individually to the wrapper
        process.StartInfo.ArgumentList.Add(exe);
        for (int i = 1; i < parts.Length; i++)
        {
            process.StartInfo.ArgumentList.Add(parts[i]);
        }

        process.OutputDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                UpdateOutput(commandInfo, e.Data + "\n");
            }
        };
        process.ErrorDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                UpdateOutput(commandInfo, e.Data + "\n");
            }
        };

        process.Exited += (sender, e) =>
        {
            commandInfo.ExitCode = process.ExitCode;
            commandInfo.Process = null;

            if (commandInfo.ExitCode.HasValue && commandInfo.ExitCode != 0)
            {
                // Execution failed, stop here
                UpdateOutput(commandInfo, $"Executable {index + 1} failed with exit code {commandInfo.ExitCode}. Stopping.\n");
                commandInfo.StatusChanged?.Invoke();
                commandInfo.ButtonsChanged?.Invoke();
                GLib.Functions.IdleAdd(0, () =>
                {
                    UpdateOutput(commandInfo, $"Process exited with code {commandInfo.ExitCode}.\n");
                    process.Dispose();
                    return false;
                });
            }
            else
            {
                // Success, move to next executable
                process.Dispose();
                ExecuteNextExecutable(commandInfo, index + 1);
            }
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            commandInfo.Process = process;
            commandInfo.StatusChanged?.Invoke();
        }
        catch (Exception ex)
        {
            UpdateOutput(commandInfo, $"Error starting process: {ex.Message}\n");
            // On error, stop execution
            commandInfo.ExitCode = -1;
            commandInfo.Process = null;
            commandInfo.StatusChanged?.Invoke();
            commandInfo.ButtonsChanged?.Invoke();
        }
    }

    private static void UpdateOutput(CommandInfo commandInfo, string? data)
    {
        if (string.IsNullOrEmpty(data) || commandInfo.OutputBuffer == null)
        {
            return;
        }

        GLib.Functions.IdleAdd(0, () =>
        {
            var buffer = commandInfo.OutputBuffer;
            var scroll = commandInfo.TerminalScroll;

            // Check if we are at the bottom BEFORE adding text
            if (scroll?.Vadjustment != null)
            {
                var adj = scroll.Vadjustment;
                commandInfo.IsAutoScrolling = adj.Value >= (adj.Upper - adj.PageSize - 5.0);
            }

            // Insert cleanly at the end
            var normalizedData = data.Replace("\r\n", "\n").Replace("\r", "\n");
            buffer.GetEndIter(out var endIter);
            buffer.Insert(endIter, normalizedData, -1);

            // Cleanly truncate older text
            if (buffer.GetLineCount() > 500)
            {
                buffer.GetIterAtLine(out var startDelete, 0);
                buffer.GetIterAtLine(out var endDelete, buffer.GetLineCount() - 500);
                buffer.Delete(startDelete, endDelete);
            }

            return false;
        });
    }

    private static void StopProcess(CommandInfo commandInfo)
    {
        if (commandInfo.Process is { HasExited: false } process)
        {
            try
            {
                // Send SIGTERM (15) instead of process.Kill()
                // This lets the wrapper shut down its own children cleanly
                var result = kill(process.Id, 15);

                if (result != 0)
                {
                    // Fallback just in case the process is completely frozen
                    process.Kill();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to stop process: {ex.Message}");
            }
        }
    }

    // Add this native import inside your Application class
    [DllImport("libc.so.6", SetLastError = true)]
    private static extern int kill(int pid, int sig);


    private void SaveCommands()
    {
        try
        {
            var directory = Path.GetDirectoryName(GetStoragePath());
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var commandsToSave = _commands.Select(c => new CommandData
            {
                Name = c.Name,
                Executable = string.Join("\n", c.Executables),
                WorkingDirectory = c.WorkingDirectory
            }).ToList();

            var json = JsonSerializer.Serialize(commandsToSave, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(GetStoragePath(), json);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to save commands: {ex.Message}");
        }
    }

    private void LoadCommands()
    {
        try
        {
            if (!File.Exists(GetStoragePath()))
            {
                return;
            }

            var json = File.ReadAllText(GetStoragePath());
            var commandsData = JsonSerializer.Deserialize<List<CommandData>>(json);

            if (commandsData != null)
            {
                _commands = [.. commandsData.Select(cd => new CommandInfo(
                    cd.Name,
                    cd.Executable,
                    cd.WorkingDirectory,
                    false))];
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load commands: {ex.Message}");
        }
    }
}

public class CommandInfo(string name, string executable, string? workingDirectory, bool isNew)
{
    public string Name { get; set; } = name;
    public List<string> Executables { get; set; } = ParseExecutables(executable);
    public string? WorkingDirectory { get; set; } = workingDirectory;
    public bool IsNew { get; set; } = isNew;
    public Process? Process { get; set; }
    public int? ExitCode { get; set; }
    public TextBuffer? OutputBuffer { get; set; }
    public Action? StatusChanged { get; set; }
    public Action? ButtonsChanged { get; set; }
    public ScrolledWindow? TerminalScroll { get; set; }
    public bool IsAutoScrolling { get; set; } = true;

    private static List<string> ParseExecutables(string execStr)
    {
        if (string.IsNullOrWhiteSpace(execStr))
        {
            return [];
        }

        // Support both comma-separated and newline-separated executables
        return execStr.Split(new[] { ',', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                      .Select(e => e.Trim())
                      .Where(e => !string.IsNullOrEmpty(e))
                      .ToList();
    }
}

public class CommandData
{
    public required string Name { get; set; }
    public required string Executable { get; set; }
    public string? WorkingDirectory { get; set; }
}
