// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Certificates;
using Aspire.Cli.Configuration;
using Aspire.Cli.Dcp;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Aspire.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using StreamJsonRpc;

namespace Aspire.Cli.Commands;

internal sealed class RunCommand : BaseCommand
{
    // Constants for running instance detection
    private const int ProcessTerminationTimeoutMs = 10000; // Wait up to 10 seconds for processes to terminate
    private const int ProcessTerminationPollIntervalMs = 250; // Check process status every 250ms

    private readonly IDotNetCliRunner _runner;
    private readonly IInteractionService _interactionService;
    private readonly ICertificateService _certificateService;
    private readonly IProjectLocator _projectLocator;
    private readonly IAnsiConsole _ansiConsole;
    private readonly AspireCliTelemetry _telemetry;
    private readonly IConfiguration _configuration;
    private readonly IDotNetSdkInstaller _sdkInstaller;
    private readonly IServiceProvider _serviceProvider;
    private readonly IFeatures _features;
    private readonly ICliHostEnvironment _hostEnvironment;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RunCommand> _logger;
    private readonly IDcpLauncher _dcpLauncher;
    private readonly IDcpClient _dcpClient;

    public RunCommand(
        IDotNetCliRunner runner,
        IInteractionService interactionService,
        ICertificateService certificateService,
        IProjectLocator projectLocator,
        IAnsiConsole ansiConsole,
        AspireCliTelemetry telemetry,
        IConfiguration configuration,
        IDotNetSdkInstaller sdkInstaller,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        IServiceProvider serviceProvider,
        CliExecutionContext executionContext,
        ICliHostEnvironment hostEnvironment,
        IDcpLauncher dcpLauncher,
        IDcpClient dcpClient,
        ILogger<RunCommand> logger,
        TimeProvider? timeProvider)
        : base("run", RunCommandStrings.Description, features, updateNotifier, executionContext, interactionService)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(interactionService);
        ArgumentNullException.ThrowIfNull(certificateService);
        ArgumentNullException.ThrowIfNull(projectLocator);
        ArgumentNullException.ThrowIfNull(ansiConsole);
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(sdkInstaller);
        ArgumentNullException.ThrowIfNull(hostEnvironment);
        ArgumentNullException.ThrowIfNull(dcpLauncher);
        ArgumentNullException.ThrowIfNull(dcpClient);
        ArgumentNullException.ThrowIfNull(logger);

        _runner = runner;
        _interactionService = interactionService;
        _certificateService = certificateService;
        _projectLocator = projectLocator;
        _ansiConsole = ansiConsole;
        _telemetry = telemetry;
        _configuration = configuration;
        _serviceProvider = serviceProvider;
        _sdkInstaller = sdkInstaller;
        _features = features;
        _hostEnvironment = hostEnvironment;
        _dcpLauncher = dcpLauncher;
        _dcpClient = dcpClient;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;

        var projectOption = new Option<FileInfo?>("--project");
        projectOption.Description = RunCommandStrings.ProjectArgumentDescription;
        Options.Add(projectOption);

        if (ExtensionHelper.IsExtensionHost(InteractionService, out _, out _))
        {
            var startDebugOption = new Option<bool>("--start-debug-session");
            startDebugOption.Description = RunCommandStrings.StartDebugSessionArgumentDescription;
            Options.Add(startDebugOption);
        }

        TreatUnmatchedTokensAsErrors = false;
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var passedAppHostProjectFile = parseResult.GetValue<FileInfo?>("--project");
        var isExtensionHost = ExtensionHelper.IsExtensionHost(InteractionService, out _, out _);
        var startDebugSession = isExtensionHost && parseResult.GetValue<bool>("--start-debug-session");
        var runningInstanceDetectionEnabled = _features.IsFeatureEnabled(KnownFeatures.RunningInstanceDetectionEnabled, defaultValue: true);
        // Force option kept for backward compatibility but no longer used since prompt was removed
        // var force = runningInstanceDetectionEnabled && parseResult.GetValue<bool>("--force");

        // A user may run `aspire run` in an Aspire terminal in VS Code. In this case, intercept and prompt
        // VS Code to start a debug session using the current directory
        if (ExtensionHelper.IsExtensionHost(InteractionService, out var extensionInteractionService, out _)
            && string.IsNullOrEmpty(_configuration[KnownConfigNames.ExtensionDebugSessionId]))
        {
            extensionInteractionService.DisplayConsolePlainText(RunCommandStrings.StartingDebugSessionInExtension);
            await extensionInteractionService.StartDebugSessionAsync(ExecutionContext.WorkingDirectory.FullName, passedAppHostProjectFile?.FullName, startDebugSession);
            return ExitCodeConstants.Success;
        }

        // Check if the .NET SDK is available
        if (!await SdkInstallHelper.EnsureSdkInstalledAsync(_sdkInstaller, InteractionService, _features, _hostEnvironment, cancellationToken))
        {
            return ExitCodeConstants.SdkNotInstalled;
        }

        var buildOutputCollector = new OutputCollector();
        var runOutputCollector = new OutputCollector();

        (bool IsCompatibleAppHost, bool SupportsBackchannel, AppHostInfo? Info)? appHostCompatibilityCheck = null;
        try
        {
            using var activity = _telemetry.ActivitySource.StartActivity(this.Name);

            var effectiveAppHostFile = await _projectLocator.UseOrFindAppHostProjectFileAsync(passedAppHostProjectFile, createSettingsFile: true, cancellationToken);

            if (effectiveAppHostFile is null)
            {
                return ExitCodeConstants.FailedToFindProject;
            }

            // Check for running instance if feature is enabled
            if (runningInstanceDetectionEnabled)
            {
                // Even if we fail to stop we won't block the apphost starting
                // to make sure we don't ever break flow. It should mostly stop
                // just fine though.
                await CheckAndHandleRunningInstanceAsync(effectiveAppHostFile, cancellationToken);
            }

            var isSingleFileAppHost = effectiveAppHostFile.Extension != ".csproj";

            var env = new Dictionary<string, string>();

            var debug = parseResult.GetValue<bool>("--debug");

            var waitForDebugger = parseResult.GetValue<bool>("--wait-for-debugger");

            if (waitForDebugger)
            {
                env[KnownConfigNames.WaitForDebugger] = "true";
            }

            await _certificateService.EnsureCertificatesTrustedAsync(_runner, cancellationToken);

            var watch = !isSingleFileAppHost && (_features.IsFeatureEnabled(KnownFeatures.DefaultWatchEnabled, defaultValue: false) || (isExtensionHost && !startDebugSession));

            if (!watch)
            {
                if (!isSingleFileAppHost && !isExtensionHost)
                {
                    var buildOptions = new DotNetCliRunnerInvocationOptions
                    {
                        StandardOutputCallback = buildOutputCollector.AppendOutput,
                        StandardErrorCallback = buildOutputCollector.AppendError,
                    };

                    var buildExitCode = await AppHostHelper.BuildAppHostAsync(_runner, InteractionService, effectiveAppHostFile, buildOptions, ExecutionContext.WorkingDirectory, cancellationToken);

                    if (buildExitCode != 0)
                    {
                        InteractionService.DisplayLines(buildOutputCollector.GetLines());
                        InteractionService.DisplayError(InteractionServiceStrings.ProjectCouldNotBeBuilt);
                        return ExitCodeConstants.FailedToBuildArtifacts;
                    }
                }
            }

            if (isSingleFileAppHost)
            {
                // TODO: Add logic to read SDK version from *.cs file.
                appHostCompatibilityCheck = (true, true, new AppHostInfo(
                    IsAspireHost: true,
                    AspireHostingVersion: VersionHelper.GetDefaultTemplateVersion(),
                    DcpCliPath: null,
                    DcpExtensionsPath: null,
                    DcpBinPath: null,
                    DashboardPath: null,
                    ContainerRuntime: null));
            }
            else
            {
                appHostCompatibilityCheck = await AppHostHelper.CheckAppHostCompatibilityAsync(_runner, InteractionService, effectiveAppHostFile, _telemetry, ExecutionContext.WorkingDirectory, cancellationToken);
            }

            if (!appHostCompatibilityCheck?.IsCompatibleAppHost ?? throw new InvalidOperationException(RunCommandStrings.IsCompatibleAppHostIsNull))
            {
                return ExitCodeConstants.FailedToDotnetRunAppHost;
            }

            // Launch CLI-owned DCP if the feature is enabled
            DcpSession? dcpSession = null;
            var dcpEnabled = _features.IsFeatureEnabled(KnownFeatures.DcpEnabled, defaultValue: false);
            _logger.LogDebug("CLI-owned DCP feature enabled: {DcpEnabled}", dcpEnabled);

            if (dcpEnabled)
            {
                var appHostInfo = appHostCompatibilityCheck?.Info;
                _logger.LogDebug("DcpCliPath from AppHost: {DcpCliPath}", appHostInfo?.DcpCliPath ?? "(null)");

                if (appHostInfo != null && !string.IsNullOrEmpty(appHostInfo.DcpCliPath))
                {
                    try
                    {
                        dcpSession = await InteractionService.ShowStatusAsync(
                            "Starting DCP...",
                            async () => await _dcpLauncher.LaunchAsync(appHostInfo, cancellationToken));

                        // Pass kubeconfig path to AppHost so it knows to use CLI-owned DCP
                        env["DCP_KUBECONFIG_PATH"] = dcpSession.KubeconfigPath;
                        _logger.LogDebug("CLI-owned DCP started with kubeconfig at {KubeconfigPath}", dcpSession.KubeconfigPath);

                        // Create CLI-owned dashboard via DCP
                        await CreateCliOwnedDashboardAsync(
                            appHostInfo,
                            effectiveAppHostFile,
                            dcpSession,
                            env,
                            cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        InteractionService.DisplayError($"Failed to start DCP: {ex.Message}");
                        return ExitCodeConstants.FailedToDotnetRunAppHost;
                    }
                }
                else
                {
                    _logger.LogDebug("CLI-owned DCP skipped: DcpCliPath not available");
                }
            }

            // When CLI owns the dashboard, we need to use --no-launch-profile to ensure
            // our environment variables are passed to the AppHost (otherwise dotnet run may not inherit them)
            var useNoLaunchProfile = env.ContainsKey("ASPIRE_CLI_DASHBOARD_MODE");

            var runOptions = new DotNetCliRunnerInvocationOptions
            {
                StandardOutputCallback = runOutputCollector.AppendOutput,
                StandardErrorCallback = runOutputCollector.AppendError,
                StartDebugSession = startDebugSession,
                Debug = debug,
                NoLaunchProfile = useNoLaunchProfile
            };

            var backchannelCompletitionSource = new TaskCompletionSource<IAppHostCliBackchannel>();

            var unmatchedTokens = parseResult.UnmatchedTokens.ToArray();

            if (isSingleFileAppHost)
            {
                // TODO:  This is just fallback behavior for now. We need to decide on whether we
                //        want to treat the lack of a apphost.run.json as an error or whether we
                //        want to somehow manage this information in .aspire/settings.json and how
                //        this might work in polyglot scenarios. For the preview of this feature
                //        I'm not over investing too much time in this :)
                var runJsonFilePath = effectiveAppHostFile.FullName[..^2] + "run.json";
                if (!File.Exists(runJsonFilePath))
                {
                    env["ASPNETCORE_ENVIRONMENT"] = "Development";
                    env["DOTNET_ENVIRONMENT"] = "Development";
                    env["ASPNETCORE_URLS"] = "https://localhost:17193;http://localhost:15069";
                    env["ASPIRE_DASHBOARD_MCP_ENDPOINT_URL"] = "https://localhost:21294";
                    env["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"] = "https://localhost:21293";
                    env["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"] = "https://localhost:22086";
                }
            }

            var pendingRun = _runner.RunAsync(
                effectiveAppHostFile,
                watch,
                !watch,
                unmatchedTokens,
                env,
                backchannelCompletitionSource,
                runOptions,
                cancellationToken);

            // Wait for the backchannel to be established.
            var backchannel = await InteractionService.ShowStatusAsync(isExtensionHost ? InteractionServiceStrings.BuildingAppHost : RunCommandStrings.ConnectingToAppHost, async () => { return await backchannelCompletitionSource.Task.WaitAsync(cancellationToken); });

            var logFile = GetAppHostLogFile();

            var pendingLogCapture = CaptureAppHostLogsAsync(logFile, backchannel, _interactionService, cancellationToken);

            var dashboardUrls = await InteractionService.ShowStatusAsync(RunCommandStrings.StartingDashboard, async () => { return await backchannel.GetDashboardUrlsAsync(cancellationToken); });

            if (dashboardUrls.DashboardHealthy is false)
            {
                InteractionService.DisplayError(RunCommandStrings.DashboardFailedToStart);
                InteractionService.DisplayLines(runOutputCollector.GetLines());
                return ExitCodeConstants.DashboardFailure;
            }

            _ansiConsole.WriteLine();
            var topGrid = new Grid();
            topGrid.AddColumn();
            topGrid.AddColumn();

            var topPadder = new Padder(topGrid, new Padding(3, 0));

            var dashboardsLocalizedString = RunCommandStrings.Dashboard;
            var logsLocalizedString = RunCommandStrings.Logs;
            var endpointsLocalizedString = RunCommandStrings.Endpoints;
            var appHostLocalizedString = RunCommandStrings.AppHost;

            var longestLocalizedLength = new[] { dashboardsLocalizedString, logsLocalizedString, endpointsLocalizedString, appHostLocalizedString }
                .Max(s => s.Length);

            // +1 -> accommodates the colon (:) that gets appended to each localized string
            var longestLocalizedLengthWithColon = longestLocalizedLength + 1;

            topGrid.Columns[0].Width = longestLocalizedLengthWithColon;

            var appHostRelativePath = Path.GetRelativePath(ExecutionContext.WorkingDirectory.FullName, effectiveAppHostFile.FullName);
            topGrid.AddRow(new Align(new Markup($"[bold green]{appHostLocalizedString}[/]:"), HorizontalAlignment.Right), new Text(appHostRelativePath));
            topGrid.AddRow(Text.Empty, Text.Empty);

            if (!isExtensionHost)
            {
                topGrid.AddRow(new Align(new Markup($"[bold green]{dashboardsLocalizedString}[/]:"), HorizontalAlignment.Right), new Markup($"[link={dashboardUrls.BaseUrlWithLoginToken}]{dashboardUrls.BaseUrlWithLoginToken}[/]"));
                if (dashboardUrls.CodespacesUrlWithLoginToken is { } codespacesUrlWithLoginToken)
                {
                    topGrid.AddRow(Text.Empty, new Markup($"[link={codespacesUrlWithLoginToken}]{codespacesUrlWithLoginToken}[/]"));
                }
            }

            topGrid.AddRow(Text.Empty, Text.Empty);
            topGrid.AddRow(new Align(new Markup($"[bold green]{logsLocalizedString}[/]:"), HorizontalAlignment.Right), new Text(logFile.FullName));

            _ansiConsole.Write(topPadder);

            // Use the presence of CodespacesUrlWithLoginToken to detect codespaces, as this is more reliable
            // than environment variables since it comes from the same backend detection logic
            var isCodespaces = dashboardUrls.CodespacesUrlWithLoginToken is not null;
            var isRemoteContainers = _configuration.GetValue<bool>("REMOTE_CONTAINERS", false);
            var isSshRemote = _configuration.GetValue<string?>("VSCODE_IPC_HOOK_CLI") is not null
                              && _configuration.GetValue<string?>("SSH_CONNECTION") is not null;

            AppendCtrlCMessage(longestLocalizedLengthWithColon);

            if (isCodespaces || isRemoteContainers || isSshRemote)
            {
                bool firstEndpoint = true;

                try
                {
                    var resourceStates = backchannel.GetResourceStatesAsync(cancellationToken);
                    await foreach (var resourceState in resourceStates.WithCancellation(cancellationToken))
                    {
                        ProcessResourceState(resourceState, (resource, endpoint) =>
                        {
                            // When we are appending endpoints we need
                            // to remove the CTRL-C message that was appended
                            // previously. So we can write the endpoint.
                            // We will append the CTRL-C message again after
                            // writing the endpoint.
                            ClearLines(2);

                            var endpointsGrid = new Grid();
                            endpointsGrid.AddColumn();
                            endpointsGrid.AddColumn();
                            endpointsGrid.Columns[0].Width = longestLocalizedLengthWithColon;

                            if (firstEndpoint)
                            {
                                endpointsGrid.AddRow(Text.Empty, Text.Empty);
                            }

                            endpointsGrid.AddRow(
                                firstEndpoint ? new Align(new Markup($"[bold green]{endpointsLocalizedString}[/]:"), HorizontalAlignment.Right) : Text.Empty,
                                new Markup($"[bold]{resource}[/] [grey]has endpoint[/] [link={endpoint}]{endpoint}[/]")
                            );

                            var endpointsPadder = new Padder(endpointsGrid, new Padding(3, 0));
                            _ansiConsole.Write(endpointsPadder);
                            firstEndpoint = false;

                            AppendCtrlCMessage(longestLocalizedLengthWithColon);
                        });
                    }
                }
                catch (ConnectionLostException) when (cancellationToken.IsCancellationRequested)
                {
                    // Just swallow this exception because this is an orderly shutdown of the backchannel.
                }
            }

            if (ExtensionHelper.IsExtensionHost(InteractionService, out extensionInteractionService, out _))
            {
                extensionInteractionService.DisplayDashboardUrls(dashboardUrls);
                extensionInteractionService.NotifyAppHostStartupCompleted();
            }

            await pendingLogCapture;
            return await pendingRun;
        }
        catch (OperationCanceledException ex) when (ex.CancellationToken == cancellationToken || ex is ExtensionOperationCanceledException)
        {
            InteractionService.DisplayCancellationMessage();
            return ExitCodeConstants.Success;
        }
        catch (ProjectLocatorException ex)
        {
            return HandleProjectLocatorException(ex, InteractionService);
        }
        catch (AppHostIncompatibleException ex)
        {
            return InteractionService.DisplayIncompatibleVersionError(
                ex,
                appHostCompatibilityCheck?.Info?.AspireHostingVersion ?? throw new InvalidOperationException(ErrorStrings.AspireHostingVersionNull)
                );
        }
        catch (CertificateServiceException ex)
        {
            InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, TemplatingStrings.CertificateTrustError, ex.Message.EscapeMarkup()));
            return ExitCodeConstants.FailedToTrustCertificates;
        }
        catch (FailedToConnectBackchannelConnection ex)
        {
            InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.ErrorConnectingToAppHost, ex.Message.EscapeMarkup()));
            InteractionService.DisplayLines(runOutputCollector.GetLines());
            return ExitCodeConstants.FailedToDotnetRunAppHost;
        }
        catch (Exception ex)
        {
            InteractionService.DisplayError(string.Format(CultureInfo.CurrentCulture, InteractionServiceStrings.UnexpectedErrorOccurred, ex.Message.EscapeMarkup()));
            InteractionService.DisplayLines(runOutputCollector.GetLines());
            return ExitCodeConstants.FailedToDotnetRunAppHost;
        }
    }

    private void ClearLines(int lines)
    {
        if (lines <= 0)
        {
            return;
        }

        for (var i = 0; i < lines; i++)
        {
            _ansiConsole.Write("\u001b[1A");
            _ansiConsole.Write("\u001b[2K"); // Clear the line
        }
    }

    private void AppendCtrlCMessage(int longestLocalizedLengthWithColon)
    {
        if (ExtensionHelper.IsExtensionHost(_interactionService, out _, out _))
        {
            return;
        }

        var ctrlCGrid = new Grid();
        ctrlCGrid.AddColumn();
        ctrlCGrid.AddColumn();
        ctrlCGrid.Columns[0].Width = longestLocalizedLengthWithColon;
        ctrlCGrid.AddRow(Text.Empty, Text.Empty);
        ctrlCGrid.AddRow(new Text(string.Empty), new Markup(RunCommandStrings.PressCtrlCToStopAppHost) { Overflow = Overflow.Ellipsis });

        var ctrlCPadder = new Padder(ctrlCGrid, new Padding(3, 0));
        _ansiConsole.Write(ctrlCPadder);
    }

    private FileInfo GetAppHostLogFile()
    {
        var homeDirectory = ExecutionContext.HomeDirectory.FullName;
        var logsPath = Path.Combine(homeDirectory, ".aspire", "cli", "logs");
        var logFilePath = Path.Combine(logsPath, $"apphost-{Environment.ProcessId}-{_timeProvider.GetUtcNow():yyyy-MM-dd-HH-mm-ss}.log");
        var logFile = new FileInfo(logFilePath);
        return logFile;
    }

    private static async Task CaptureAppHostLogsAsync(FileInfo logFile, IAppHostCliBackchannel backchannel, IInteractionService interactionService, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Yield();

            if (!logFile.Directory!.Exists)
            {
                logFile.Directory.Create();
            }

            using var streamWriter = new StreamWriter(logFile.FullName, append: true)
            {
                AutoFlush = true
            };

            var logEntries = backchannel.GetAppHostLogEntriesAsync(cancellationToken);

            await foreach (var entry in logEntries.WithCancellation(cancellationToken))
            {
                if (ExtensionHelper.IsExtensionHost(interactionService, out var extensionInteractionService, out _))
                {
                    if (entry.LogLevel is not LogLevel.Trace and not LogLevel.Debug)
                    {
                        // Send only information+ level logs to the extension host.
                        extensionInteractionService.WriteDebugSessionMessage(entry.Message, entry.LogLevel is not LogLevel.Error and not LogLevel.Critical, "\x1b[2m");
                    }
                }

                await streamWriter.WriteLineAsync($"{entry.Timestamp:HH:mm:ss} [{entry.LogLevel}] {entry.CategoryName}: {entry.Message}");
            }
        }
        catch (OperationCanceledException)
        {
            // Swallow the exception if the operation was cancelled.
            return;
        }
        catch (ConnectionLostException) when (cancellationToken.IsCancellationRequested)
        {
            // Just swallow this exception because this is an orderly shutdown of the backchannel.
            return;
        }
    }

    private readonly Dictionary<string, RpcResourceState> _resourceStates = new();

    public void ProcessResourceState(RpcResourceState resourceState, Action<string, string> endpointWriter)
    {
        if (_resourceStates.TryGetValue(resourceState.Resource, out var existingResourceState))
        {
            if (resourceState.Endpoints.Except(existingResourceState.Endpoints) is { } endpoints && endpoints.Any())
            {
                foreach (var endpoint in endpoints)
                {
                    endpointWriter(resourceState.Resource, endpoint);
                }
            }

            _resourceStates[resourceState.Resource] = resourceState;
        }
        else
        {
            if (resourceState.Endpoints is { } endpoints && endpoints.Any())
            {
                foreach (var endpoint in endpoints)
                {
                    endpointWriter(resourceState.Resource, endpoint);
                }
            }

            _resourceStates[resourceState.Resource] = resourceState;
        }
    }

    private string ComputeAuxiliarySocketPath(string appHostPath)
    {
        return AppHostHelper.ComputeAuxiliarySocketPath(appHostPath, ExecutionContext.HomeDirectory.FullName);
    }

    private async Task<bool> CheckAndHandleRunningInstanceAsync(FileInfo appHostFile, CancellationToken cancellationToken)
    {
        var auxiliarySocketPath = ComputeAuxiliarySocketPath(appHostFile.FullName);

        // Check if the socket file exists
        if (!File.Exists(auxiliarySocketPath))
        {
            return true; // No running instance, continue
        }

        // Stop the running instance (no prompt per mitchdenny's request)
        var stopped = await StopRunningInstanceAsync(auxiliarySocketPath, cancellationToken);
        
        return stopped;
    }

    private async Task<bool> StopRunningInstanceAsync(string socketPath, CancellationToken cancellationToken)
    {
        try
        {
            // Connect to the auxiliary backchannel using the new encapsulated class
            using var backchannel = await AppHostAuxiliaryBackchannel.ConnectAsync(socketPath, _logger, cancellationToken).ConfigureAwait(false);

            // Get the AppHost information (already retrieved during connection, but we need it)
            var appHostInfo = backchannel.AppHostInfo;

            if (appHostInfo is null)
            {
                _logger.LogDebug("Failed to stop running instance because appHostInfo was null. This may indicate the backchannel connection was established but no AppHost information was received.");
                return false;
            }

            // Display message that we're stopping the previous instance
            var cliPidText = appHostInfo.CliProcessId.HasValue ? appHostInfo.CliProcessId.Value.ToString(CultureInfo.InvariantCulture) : "N/A";
            InteractionService.DisplayMessage("stop_sign", $"Stopping previous instance (AppHost PID: {appHostInfo.ProcessId.ToString(CultureInfo.InvariantCulture)}, CLI PID: {cliPidText})");

            // Call StopAppHostAsync on the auxiliary backchannel
            await backchannel.StopAppHostAsync(cancellationToken).ConfigureAwait(false);

            // Monitor the PIDs for termination
            var stopped = await MonitorProcessesForTerminationAsync(appHostInfo, cancellationToken).ConfigureAwait(false);

            if (stopped)
            {
                InteractionService.DisplaySuccess(RunCommandStrings.RunningInstanceStopped);
            }
            else
            {
                _logger.LogDebug("Failed to stop running instance because the process did not terminate within the {TimeoutMs}ms timeout period. The process may still be shutting down.", ProcessTerminationTimeoutMs);
            }

            return stopped;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to stop running instance due to an exception. The instance may have already exited or the connection may have been lost.");
            return false;
        }
    }

    private async Task<bool> MonitorProcessesForTerminationAsync(AppHostInformation appHostInfo, CancellationToken cancellationToken)
    {
        var startTime = _timeProvider.GetUtcNow();
        var pidsToMonitor = new List<int> { appHostInfo.ProcessId };
        
        if (appHostInfo.CliProcessId.HasValue)
        {
            pidsToMonitor.Add(appHostInfo.CliProcessId.Value);
        }

        while ((_timeProvider.GetUtcNow() - startTime).TotalMilliseconds < ProcessTerminationTimeoutMs)
        {
            var allStopped = true;
            
            foreach (var pid in pidsToMonitor)
            {
                try
                {
                    var process = Process.GetProcessById(pid);
                    // If we can get the process, it's still running
                    allStopped = false;
                }
                catch (ArgumentException)
                {
                    // Process doesn't exist, it has stopped
                }
            }

            if (allStopped)
            {
                return true;
            }

            await Task.Delay(ProcessTerminationPollIntervalMs, cancellationToken).ConfigureAwait(false);
        }

        // Timeout reached
        return false;
    }

    private async Task CreateCliOwnedDashboardAsync(
        AppHostInfo appHostInfo,
        FileInfo appHostProjectFile,
        DcpSession dcpSession,
        Dictionary<string, string> env,
        CancellationToken cancellationToken)
    {
        // Read launchSettings.json from AppHost project to get dashboard configuration
        var launchSettings = LaunchSettingsReader.ReadLaunchSettings(appHostProjectFile.FullName);
        var launchProfile = LaunchSettingsReader.GetEffectiveLaunchProfile(launchSettings);

        if (launchProfile == null)
        {
            _logger.LogDebug("Could not find launch profile in launchSettings.json. Dashboard will be launched by AppHost.");
            return;
        }

        if (string.IsNullOrEmpty(appHostInfo.DashboardPath))
        {
            _logger.LogDebug("Dashboard path not available. Dashboard will be launched by AppHost.");
            return;
        }

        var profileEnv = launchProfile.EnvironmentVariables;

        // Get configuration from launchSettings
        var resourceServiceUrl = profileEnv.GetValueOrDefault("ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL");
        var otlpGrpcUrl = profileEnv.GetValueOrDefault("ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL");
        var otlpHttpUrl = profileEnv.GetValueOrDefault("ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL");
        var mcpUrl = profileEnv.GetValueOrDefault("ASPIRE_DASHBOARD_MCP_ENDPOINT_URL");
        var aspnetEnvironment = profileEnv.GetValueOrDefault("ASPNETCORE_ENVIRONMENT") ?? "Production";

        if (string.IsNullOrEmpty(resourceServiceUrl))
        {
            _logger.LogDebug("ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL not found in launchSettings. Dashboard will be launched by AppHost.");
            return;
        }

        // Generate tokens/keys that will be shared between CLI, AppHost, and Dashboard
        var browserToken = TokenGenerator.GenerateToken();
        var resourceServiceApiKey = TokenGenerator.GenerateToken();
        var otlpApiKey = TokenGenerator.GenerateToken();
        var mcpApiKey = TokenGenerator.GenerateToken();

        // Dashboard frontend URL - use applicationUrl from launchSettings or default
        var dashboardFrontendUrl = launchProfile.ApplicationUrl ?? "http://localhost:18888";
        // Take just the first URL if there are multiple
        if (dashboardFrontendUrl.Contains(';'))
        {
            dashboardFrontendUrl = dashboardFrontendUrl.Split(';')[0];
        }

        _logger.LogDebug("Connecting to DCP at {KubeconfigPath}", dcpSession.KubeconfigPath);
        await _dcpClient.ConnectAsync(dcpSession.KubeconfigPath, cancellationToken);

        // Create Service resources for dashboard endpoints
        // This is how DCP normally tracks endpoint allocation
        // Services will become Ready once the dashboard binds to their ports
        await CreateDashboardServiceAsync("aspire-dashboard-http", dashboardFrontendUrl, cancellationToken);

        if (!string.IsNullOrEmpty(otlpGrpcUrl))
        {
            await CreateDashboardServiceAsync("aspire-dashboard-otlp-grpc", otlpGrpcUrl, cancellationToken);
        }

        if (!string.IsNullOrEmpty(otlpHttpUrl))
        {
            await CreateDashboardServiceAsync("aspire-dashboard-otlp-http", otlpHttpUrl, cancellationToken);
        }

        if (!string.IsNullOrEmpty(mcpUrl))
        {
            await CreateDashboardServiceAsync("aspire-dashboard-mcp", mcpUrl, cancellationToken);
        }

        // Build dashboard environment variables using the URLs from launchSettings
        // The Services ensure DCP tracks these endpoints properly
        var dashboardEnv = new Dictionary<string, string>
        {
            // Core URLs
            ["ASPNETCORE_URLS"] = dashboardFrontendUrl,
            ["ASPNETCORE_ENVIRONMENT"] = aspnetEnvironment,
            ["DOTNET_RESOURCE_SERVICE_ENDPOINT_URL"] = resourceServiceUrl,

            // Frontend auth
            ["DASHBOARD__FRONTEND__AUTHMODE"] = "BrowserToken",
            ["DASHBOARD__FRONTEND__BROWSERTOKEN"] = browserToken,

            // Resource service client auth
            ["DASHBOARD__RESOURCESERVICECLIENT__AUTHMODE"] = "ApiKey",
            ["DASHBOARD__RESOURCESERVICECLIENT__APIKEY"] = resourceServiceApiKey,

            // Logging
            ["LOGGING__CONSOLE__FORMATTERNAME"] = "json"
        };

        // Add OTLP endpoints if configured
        if (!string.IsNullOrEmpty(otlpGrpcUrl))
        {
            dashboardEnv["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"] = otlpGrpcUrl;
            dashboardEnv["DASHBOARD__OTLP__AUTHMODE"] = "ApiKey";
            dashboardEnv["DASHBOARD__OTLP__PRIMARYAPIKEY"] = otlpApiKey;
        }

        if (!string.IsNullOrEmpty(otlpHttpUrl))
        {
            dashboardEnv["ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"] = otlpHttpUrl;
        }

        // Add MCP endpoint if configured
        if (!string.IsNullOrEmpty(mcpUrl))
        {
            dashboardEnv["ASPIRE_DASHBOARD_MCP_ENDPOINT_URL"] = mcpUrl;
            dashboardEnv["DASHBOARD__MCP__AUTHMODE"] = "ApiKey";
            dashboardEnv["DASHBOARD__MCP__PRIMARYAPIKEY"] = mcpApiKey;
            dashboardEnv["DASHBOARD__MCP__USECLIMCP"] = "true";
        }

        _logger.LogDebug("Creating dashboard executable in DCP");

        var dashboardSpec = new DcpExecutableSpec(
            Name: "aspire-dashboard",
            ExecutablePath: "dotnet",
            WorkingDirectory: Path.GetDirectoryName(appHostInfo.DashboardPath),
            Args: [appHostInfo.DashboardPath],
            Env: dashboardEnv);

        await _dcpClient.CreateExecutableAsync(dashboardSpec, cancellationToken);

        // Wait for dashboard to start running
        await foreach (var execInfo in _dcpClient.WatchExecutableAsync("aspire-dashboard", cancellationToken))
        {
            _logger.LogDebug("Dashboard state: {State}", execInfo.State);
            if (execInfo.State == "Running")
            {
                _logger.LogDebug("Dashboard is running with PID {Pid}", execInfo.Pid);
                break;
            }
            else if (execInfo.State is "FailedToStart" or "Terminated" or "Finished")
            {
                _logger.LogWarning("Dashboard failed to start with state {State}", execInfo.State);
                break;
            }
        }

        // Configure AppHost with CLI dashboard info so backchannel returns correct URL
        env["ASPIRE_CLI_DASHBOARD_MODE"] = "true";
        env["ASPIRE_CLI_DASHBOARD_URL"] = dashboardFrontendUrl;
        env["ASPIRE_CLI_DASHBOARD_TOKEN"] = browserToken;

        // Copy essential env vars from launchSettings since we use --no-launch-profile
        env["ASPNETCORE_ENVIRONMENT"] = aspnetEnvironment;
        env["DOTNET_ENVIRONMENT"] = aspnetEnvironment;

        // Set ASPNETCORE_URLS from applicationUrl - AppHost needs this for Kestrel binding
        if (!string.IsNullOrEmpty(launchProfile.ApplicationUrl))
        {
            env["ASPNETCORE_URLS"] = launchProfile.ApplicationUrl;
        }

        if (!string.IsNullOrEmpty(resourceServiceUrl))
        {
            env["ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL"] = resourceServiceUrl;
        }
        if (!string.IsNullOrEmpty(otlpGrpcUrl))
        {
            env["ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL"] = otlpGrpcUrl;
        }
        if (!string.IsNullOrEmpty(otlpHttpUrl))
        {
            env["ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL"] = otlpHttpUrl;
        }
        if (!string.IsNullOrEmpty(mcpUrl))
        {
            env["ASPIRE_DASHBOARD_MCP_ENDPOINT_URL"] = mcpUrl;
        }

        // Configure AppHost with same API keys so it can authenticate with dashboard
        env["AppHost:ResourceService:AuthMode"] = "ApiKey";
        env["AppHost:ResourceService:ApiKey"] = resourceServiceApiKey;

        if (!string.IsNullOrEmpty(otlpGrpcUrl))
        {
            env["AppHost:OtlpApiKey"] = otlpApiKey;
        }

        if (!string.IsNullOrEmpty(mcpUrl))
        {
            env["AppHost:McpApiKey"] = mcpApiKey;
        }

        _logger.LogDebug("CLI-owned dashboard configured at {DashboardUrl}", dashboardFrontendUrl);
    }

    private async Task<DcpServiceResource?> CreateDashboardServiceAsync(
        string serviceName,
        string url,
        CancellationToken cancellationToken)
    {
        // Parse URL to get port and scheme
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            _logger.LogWarning("Could not parse URL {Url} for service {ServiceName}", url, serviceName);
            return null;
        }

        var port = uri.Port > 0 ? uri.Port : (uri.Scheme == "https" ? 443 : 80);

        _logger.LogDebug("Creating service {ServiceName} for {Url} on port {Port}", serviceName, url, port);

        var serviceSpec = new DcpServiceSpec(
            Name: serviceName,
            Port: port,
            Address: "localhost",
            Protocol: "TCP",
            AddressAllocationMode: "Localhost");

        return await _dcpClient.CreateServiceAsync(serviceSpec, cancellationToken);
    }
}
