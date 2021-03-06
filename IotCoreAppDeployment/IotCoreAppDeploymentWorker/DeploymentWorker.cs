// Copyright (c) Microsoft. All rights reserved.

using Microsoft.Iot.IotCoreAppProjectExtensibility;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Globalization;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;

namespace Microsoft.Iot.IotCoreAppDeployment
{
    public class DeploymentWorker
    {
        private static Microsoft.ApplicationInsights.TelemetryClient TelemetryClient;

        private CommandLineParser argsHandler = null;
        private string outputFolder = null;
        private string source = "";
        private string targetName = "";
        private string makeAppxPath = null;
        private string signToolPath = null;
        private string powershellPath = null;
        private string copyOutputToFolder = null;
        private bool keepTempFolder = false;
        private TargetPlatform targetType = TargetPlatform.ARM;
        private SdkVersion sdk = SdkVersion.SDK_10_0_10586_0;
        private DependencyConfiguration configuration = DependencyConfiguration.Debug;

        private StreamWriter outputWriter;

        private const string packageFullNameFormat = "{0}_1.0.0.0_{1}__1w720vyc4ccym";

        private const string universalSdkRootKey = @"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows Kits\Installed Roots";
        private const string universalSdkRootValue = @"KitsRoot10";

        private const string powershellRootKey = @"HKEY_LOCAL_MACHINE\Software\Microsoft\PowerShell\1\ShellIds\Microsoft.PowerShell";
        private const string powershellRootValue = @"Path";

        private const string defaultSdkVersion = "10.0.10586.0";
        private const string defaultTargetUserName = "Administrator";
        private const string defaultTargetPassword = "p@ssw0rd";
        private UserInfo credentials = new UserInfo() { UserName = defaultTargetUserName, Password = defaultTargetPassword };
        private const int QueryInterval = 3000;

        private void CreateCommandLineParser()
        {
            argsHandler = new CommandLineParser(this);
            // Required args
            argsHandler.AddRequiredArgumentWithInput("s", Resource.DeploymentWorker_SourceArgMsg, (worker, value) => { worker.source = new FileInfo(value).FullName; });
            argsHandler.AddRequiredArgumentWithInput("n", Resource.DeploymentWorker_TargetArgMsg, (worker, value) => { worker.targetName = value; });
            // Optional args
            argsHandler.AddOptionalArgumentWithInput("x", string.Format(CultureInfo.InvariantCulture, Resource.DeploymentWorker_SdkArgMsg, defaultSdkVersion),
                (worker, value) => { worker.HandleSdkVersionFromCommandLine(value); });
            argsHandler.AddOptionalArgumentWithInput("t", Resource.DeploymentWorker_TempDirArgMsg, (worker, value) => { worker.outputFolder = value; });
            argsHandler.AddOptionalArgumentWithInput("f", Resource.DeploymentWorker_ConfigArgMsg,
                (worker, value) =>
                {
                    if (!Enum.TryParse<DependencyConfiguration>(value, true, out worker.configuration))
                    {
                        throw new ArgumentException(string.Format(CultureInfo.CurrentCulture,
                            Resource.DeploymentWorker_argumentHelper_0_is_not_a_supported_configuration, value));
                    }
                });
            argsHandler.AddOptionalArgumentWithInput("a", Resource.DeploymentWorker_ArchArgMsg,
                (worker, value) =>
                {
                    if (!Enum.TryParse<TargetPlatform>(value, true, out worker.targetType))
                    {
                        throw new ArgumentException(string.Format(CultureInfo.CurrentCulture,
                            Resource.DeploymentWorker_argumentHelper_0_is_not_a_supported_target_architecture,
                            value));
                    }
                });
            argsHandler.AddOptionalArgumentWithInput("w", string.Format(CultureInfo.InvariantCulture, Resource.DeploymentWorker_TargetUserNameArgMsg, defaultTargetUserName),
                (worker, value) => { worker.credentials.UserName = value; });
            argsHandler.AddOptionalArgumentWithInput("p", string.Format(CultureInfo.InvariantCulture, Resource.DeploymentWorker_TargetPasswordArgMsg, defaultTargetPassword),
                (worker, value) => { worker.credentials.Password = value; });
            argsHandler.AddOptionalArgumentWithInput("o", Resource.DeploymentWorker_SaveAppxArgMsg, (worker, value) => { worker.copyOutputToFolder = value; });
            argsHandler.AddOptionalArgumentWithInput("x", Resource.DeploymentWorker_MakeAppxArgMsg, (worker, value) => { worker.makeAppxPath = value; });
            argsHandler.AddOptionalArgumentWithInput("g", Resource.DeploymentWorker_SignToolArgMsg, (worker, value) => { worker.signToolPath = value; });
            argsHandler.AddOptionalArgumentWithInput("w", Resource.DeploymentWorker_PowershellArgMsg, (worker, value) => { worker.powershellPath = value; });
            argsHandler.AddOptionalArgumentWithoutInput("d", Resource.DeploymentWorker_KeepTempArgMsg, (worker, value) => { worker.keepTempFolder = true; });
            argsHandler.AddHelpArgument(new string[] { "h", "help", "?" }, Resource.DeploymentWorker_HelpArgMsg);
        }

        private void HandleSdkVersionFromCommandLine(string value)
        {
            sdk = GetSdkVersionFromString(value);
            if (sdk != SdkVersion.Unknown)
            {
                return;
            }

            var sb = new StringBuilder();
            var sdkVersionMembers = typeof(SdkVersion).GetEnumValues();
            for (var i = 0; i < sdkVersionMembers.Length; i++)
            {
                var sdkVersionMember = (SdkVersion)sdkVersionMembers.GetValue(i);
                if (SdkVersion.Unknown == sdkVersionMember) continue;

                var field = typeof(SdkVersion).GetField(sdkVersionMember.ToString());
                var customAttributes = field.GetCustomAttributes(typeof(DescriptionAttribute), false);
                for (var j = 0; j < customAttributes.Length; j++)
                {
                    var descriptionAttribute = customAttributes[j] as DescriptionAttribute;
                    if (descriptionAttribute != null)
                    {
                        if (sb.Length != 0) sb.Append(", ");
                        sb.Append(descriptionAttribute.Description);
                    }
                }
            }

            throw new ArgumentException(string.Format(CultureInfo.InvariantCulture, Resource.DeploymentWorker_SupportedSdksErrorMsg, sb.ToString()));
        }

        private static SdkVersion GetSdkVersionFromString(string sdk)
        {
            var sdkVersionMembers = typeof(SdkVersion).GetEnumValues();
            for (var i = 0; i < sdkVersionMembers.Length; i++)
            {
                var enumValue = (SdkVersion)sdkVersionMembers.GetValue(i);
                var field = typeof(SdkVersion).GetField(enumValue.ToString());
                var customAttributes = field.GetCustomAttributes(typeof(DescriptionAttribute), false);
                for (var j = 0; j < customAttributes.Length; j++)
                {
                    var descriptionAttribute = customAttributes[j] as DescriptionAttribute;
                    if (null != descriptionAttribute && descriptionAttribute.Description.Equals(sdk, StringComparison.OrdinalIgnoreCase))
                    {
                        return enumValue;
                    }
                }
            }

            return SdkVersion.Unknown;
        }

        public void OutputMessage(string message)
        {
            outputWriter.WriteLine(message);
        }

        private static void ExecuteExternalProcess(string executableFileName, string arguments, string logFileName)
        {
            using (var process = new Process())
            {
                process.StartInfo.FileName = executableFileName;
                process.StartInfo.Arguments = arguments;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.Start();

                var output = new StringBuilder();
                // Using WaitForExit would be cleaner, but for some reason, it
                // hangs when using MakeAppx.  In the process of debugging that,
                // I found that this never hangs.
                while (!process.HasExited)
                {
                    output.Append(process.StandardOutput.ReadToEnd());
                    Thread.Sleep(100);
                }

                using (var logStream = new StreamWriter(logFileName))
                {
                    var errors = process.StandardError.ReadToEnd();
                    if (!string.IsNullOrEmpty(errors))
                    {
                        logStream.WriteLine("\n\n\n\nErrors:");
                        logStream.Write(errors);
                    }
                    logStream.WriteLine("\n\n\n\nFull Output:");
                    logStream.Write(output.ToString());
                }
            }
        }

        private void NotifyThatMakeAppxOrSignToolNotFound()
        {
            OutputMessage(Resource.DeploymentWorker_ToolNotFound1);
            OutputMessage(Resource.DeploymentWorker_ToolNotFound2);
            OutputMessage(Resource.DeploymentWorker_ToolNotFound3);
            OutputMessage(Resource.DeploymentWorker_ToolNotFound4);
            OutputMessage(Resource.DeploymentWorker_ToolNotFound5);
        }

        private bool CopyBaseTemplateContents(ITemplate template)
        {
            var templateContents = template.GetTemplateContents();
            foreach (var content in templateContents)
            {
                if (!content.Apply(outputFolder))
                {
                    Debug.WriteLine(string.Format(CultureInfo.InvariantCulture, Resource.DeploymentWorker_FailedToCopyFromResourcesMsg, content.AppxRelativePath));
                    return false;
                }
            }
            OutputMessage(string.Format(CultureInfo.InvariantCulture, Resource.DeploymentWorker_CopiedBaseFromResourcesMsg, outputFolder));
            return true;
        }

        private bool CopyProjectFiles(IProject project)
        {
            var appxContents = project.GetAppxContents();
            foreach (var content in appxContents)
            {
                if (!content.Apply(outputFolder))
                {
                    Debug.WriteLine(string.Format(CultureInfo.InvariantCulture, Resource.DeploymentWorker_FailedToCopyFromResourcesMsg, content.AppxRelativePath));
                    return false;
                }
            }
            OutputMessage(string.Format(CultureInfo.InvariantCulture, Resource.DeploymentWorker_CopiedFromResourcesMsg, outputFolder));
            return true;
        }

        private bool SpecializeAppxManifest(IProject project)
        {
            var appxManifestChangess = project.GetAppxContentChanges();
            foreach (var change in appxManifestChangess)
            {
                if (!change.ApplyToContent(outputFolder))
                {
                    Debug.WriteLine(Resource.DeploymentWorker_FailedToChangeAppxManifest);
                    return false;
                }
            }
            OutputMessage(Resource.DeploymentWorker_ChangedAppxManifest);
            return true;
        }

        private Task<bool> BuildProjectAsync(IProjectWithCustomBuild project)
        {
            OutputMessage(Resource.DeploymentWorker_BuildStarted);
            var buildTask = project.BuildAsync(outputFolder, outputWriter);
            if (!buildTask.Result)
            {
                OutputMessage(Resource.DeploymentWorker_BuildFailed);
                return Task.FromResult(false);
            }
            OutputMessage(Resource.DeploymentWorker_BuildSucceeded);
            return Task.FromResult(true);
        }

        private bool AddCapabilitiesToAppxManifest(IProject project)
        {
            var capabilityAdditions = project.GetCapabilities();
            foreach (var capability in capabilityAdditions)
            {
                if (!capability.ApplyToContent(outputFolder))
                {
                    Debug.WriteLine(Resource.DeploymentWorker_FailedToAddCapability);
                    return false;
                }
            }
            return true;
        }

        private bool CreateAppxMapFile(ITemplate template, IProject project, string mapFile)
        {
            var resourceMetadata = new Collection<string>();
            var appxFiles = new Collection<string>();

            if (!template.GetAppxMapContents(resourceMetadata, appxFiles, outputFolder))
            {
                Debug.WriteLine(Resource.DeploymentWorker_FailedToGetTemplateContents);
                return false;
            }
            if (!project.GetAppxMapContents(resourceMetadata, appxFiles, outputFolder))
            {
                Debug.WriteLine(Resource.DeploymentWorker_FailedToGetProjectContents);
                return false;
            }

            using (var mapFileStream = File.Create(mapFile))
            {
                using (var mapFileWriter = new StreamWriter(mapFileStream))
                {
                    mapFileWriter.WriteLine("[ResourceMetadata]");
                    foreach (var md in resourceMetadata)
                    {
                        mapFileWriter.WriteLine(md);
                    }
                    mapFileWriter.WriteLine("");
                    mapFileWriter.WriteLine("[Files]");
                    foreach (var appxFile in appxFiles)
                    {
                        mapFileWriter.WriteLine(appxFile);
                    }
                }
            }
            OutputMessage(string.Format(CultureInfo.InvariantCulture, Resource.DeploymentWorker_AppxMapCreated, mapFile));
            return true;
        }

        private bool CallMakeAppx(string makeAppxCmd, string mapFile, string outputAppx)
        {
            const string makeAppxArgsFormat = "pack /l /h sha256 /m \"{0}\" /f \"{1}\" /o /p \"{2}\"";
            var makeAppxArgs = string.Format(CultureInfo.InvariantCulture, makeAppxArgsFormat, outputFolder + @"\AppxManifest.xml", mapFile, outputAppx);
            var makeAppxLogfile = outputFolder + @"\makeappx.log";

            ExecuteExternalProcess(makeAppxCmd, makeAppxArgs, makeAppxLogfile);
            if (!File.Exists(outputAppx))
            {
                return false;
            }

            OutputMessage(Resource.DeploymentWorker_AppxCreated);
            OutputMessage(string.Format(CultureInfo.InvariantCulture, "        {0}", outputAppx));
            OutputMessage(string.Format(CultureInfo.InvariantCulture, Resource.DeploymentWorker_CreatedLogfile, makeAppxLogfile));
            return true;
        }

        private bool SignAppx(string signToolCmd, string outputAppx, string pfxFile)
        {
            const string signToolArgsFormat = "sign /fd sha256 /f \"{0}\" \"{1}\"";
            var signToolArgs = string.Format(CultureInfo.InvariantCulture, signToolArgsFormat, pfxFile, outputAppx);
            var signToolLogfile = outputFolder + @"\signtool.log";

            ExecuteExternalProcess(signToolCmd, signToolArgs, signToolLogfile);
            // TODO: how to validate this?

            OutputMessage(string.Format(CultureInfo.InvariantCulture, Resource.DeploymentWorker_AppxSignedWithPfx, signToolLogfile));
            OutputMessage(string.Format(CultureInfo.InvariantCulture, Resource.DeploymentWorker_CreatedLogfile, signToolLogfile));
            return true;
        }

        private bool CreateCertFromPfx(string powershellCmd, string pfxFile, string outputCer)
        {
            const string getCertArgsFormat = "\"Get-PfxCertificate -FilePath \'{0}\' | Export-Certificate -FilePath \'{1}\' -Type CERT\"";
            var getCertArgs = string.Format(CultureInfo.InvariantCulture, getCertArgsFormat, pfxFile, outputCer);
            var powershellLogfile = outputFolder + @"\powershell.log";

            ExecuteExternalProcess(powershellCmd, getCertArgs, powershellLogfile);

            OutputMessage(Resource.DeploymentWorker_CreatedCertFromPfx);
            OutputMessage(string.Format(CultureInfo.InvariantCulture, "        {0}", outputCer));
            OutputMessage(string.Format(CultureInfo.InvariantCulture, Resource.DeploymentWorker_CreatedLogfile, powershellLogfile));
            return true;
        }

        private bool CopyDependencyAppxFiles(ReadOnlyCollection<FileStreamInfo> dependencies, string artifactsFolder)
        {
            foreach (var dependency in dependencies)
            {
                if (!dependency.Apply(artifactsFolder))
                {
                    return false;
                }
            }
            OutputMessage(Resource.DeploymentWorker_CopiedDependencyAppxFiles);
            return true;
        }

        private static bool CopyFileAndValidate(string from, string to)
        {
            File.Copy(from, to, true);
            if (!File.Exists(to))
            {
                Debug.WriteLine(string.Format(CultureInfo.InvariantCulture, Resource.DeploymentWorker_FailedToCopy0to1, from, to));
                return false;
            }
            return true;
        }

        private bool CopyArtifacts(string outputAppx, string appxFilename, string outputCer, string cerFilename, ReadOnlyCollection<FileStreamInfo> dependencies)
        {
            // If copy is not requested, skip
            if (null == copyOutputToFolder)
            {
                return true;
            }

            if (!Directory.Exists(copyOutputToFolder))
            {
                Directory.CreateDirectory(copyOutputToFolder);
            }

            // Copy APPX
            if (!CopyFileAndValidate(outputAppx, copyOutputToFolder + @"\" + appxFilename))
            {
                return false;
            }
            // Copy .cer
            if (!CopyFileAndValidate(outputCer, copyOutputToFolder + @"\" + cerFilename))
            {
                return false;
            }
            // Copy dependencies
            foreach (var dependency in dependencies)
            {
                if (!dependency.Apply(copyOutputToFolder))
                {
                    Debug.WriteLine(string.Format(CultureInfo.InvariantCulture, Resource.DeploymentWorker_FailedToSaveAppx, copyOutputToFolder + "\\" + dependency.AppxRelativePath));
                    return false;
                }
            }
            return true;
        }

        private bool HandleUnauthenticatedDeployAppx(string outputAppx, string outputCer, ReadOnlyCollection<FileStreamInfo> dependencies, string dependencyFolder, string identityName)
        {
            var deployResult = DeployAppx(outputAppx, outputCer, dependencies, dependencyFolder, identityName);
            if (deployResult == HttpStatusCode.Unauthorized)
            {
                OutputMessage(string.Format(CultureInfo.InvariantCulture, Resource.DeploymentWorker_UnauthorizedDeployment, credentials.UserName, credentials.Password));

                CustomCredentialsForm customCredentialsForm = new CustomCredentialsForm(credentials.UserName, credentials.Password);
                var result = customCredentialsForm.ShowDialog();
                if (result == System.Windows.Forms.DialogResult.OK)
                {
                    credentials.UserName = customCredentialsForm.Username;
                    credentials.Password = customCredentialsForm.Password;
                }
                else
                {
                    return false;
                }

                deployResult = DeployAppx(outputAppx, outputCer, dependencies, dependencyFolder, identityName);
                if (deployResult == HttpStatusCode.Unauthorized)
                {
                    OutputMessage(string.Format(CultureInfo.InvariantCulture, Resource.DeploymentWorker_UnauthorizedDeployment, credentials.UserName, credentials.Password));
                }
            }

            return deployResult == HttpStatusCode.OK || deployResult == HttpStatusCode.Accepted;
        }

        private HttpStatusCode DeployAppx(string outputAppx, string outputCer, ReadOnlyCollection<FileStreamInfo> dependencies, string dependencyFolder, string identityName)
        {
            OutputMessage(string.Format(CultureInfo.InvariantCulture, Resource.DeploymentWorker_StartDeploy, targetName));

            // Create list of all APPX and CER files for deployment
            var files = new List<FileInfo>()
                    {
                        new FileInfo(outputAppx),
                        new FileInfo(outputCer),
                    };
            foreach (var dependency in dependencies)
            {
                files.Add(new FileInfo(dependencyFolder + @"\" + dependency.AppxRelativePath));
            }

            // Call WEBB Rest APIs to deploy
            var packageFullName = string.Format(CultureInfo.InvariantCulture, packageFullNameFormat, identityName, targetType.ToString());
            using (var webbHelper = new WebbHelper())
            {
                OutputMessage(Resource.DeploymentWorker_DeployAppx_starting_to_deploy_certificate_APPX_and_dependencies);
                // Attempt to uninstall existing package if found
                var uninstallTask = webbHelper.UninstallAppAsync(packageFullName, targetName, credentials);
                if (uninstallTask.Result == HttpStatusCode.OK)
                {
                    // result == OK means the package was uninstalled.
                    OutputMessage(string.Format(CultureInfo.InvariantCulture, Resource.DeploymentWorker_PreviousDeployUninstalled, packageFullName));
                }
                else if (uninstallTask.Result == HttpStatusCode.Unauthorized)
                {
                    // result == Unauthorized means the credentials were not accepted.
                    return uninstallTask.Result;
                }
                else
                {
                    // result != OK could mean that the package wasn't already installed
                    //           or it could mean that there was a problem with the uninstall
                    //           request.
                    OutputMessage(string.Format(CultureInfo.InvariantCulture, Resource.DeploymentWorker_PreviousDeployNotUninstalled, packageFullName));
                }
                // Deploy new APPX, cert, and dependency files
                var deployTask = webbHelper.DeployAppAsync(files, targetName, credentials);
                if (deployTask.Result == HttpStatusCode.Accepted)
                {
                    var result = webbHelper.PollInstallStateAsync(targetName, credentials).Result;
                    OutputMessage(string.Format(CultureInfo.InvariantCulture, Resource.DeploymentWorker_DeployFinished, packageFullName));

                    OutputMessage("\r\n\r\n***");
                    OutputMessage(string.Format(CultureInfo.InvariantCulture, "*** PackageFullName = {0}", packageFullName));
                    OutputMessage("***\r\n\r\n");

                    if (!result)
                    {
                        return HttpStatusCode.BadRequest;
                    }
                }
                else
                {
                    OutputMessage(string.Format(CultureInfo.InvariantCulture, Resource.DeploymentWorker_DeployFailed, packageFullName));
                }
                return deployTask.Result;
            }
        }

        private bool CreateAppx(ITemplate template, IProject project, string makeAppxCmd, string outputAppx)
        {
            // Copy generic base template files
            if (!CopyBaseTemplateContents(template))
            {
                return false;
            }

            // Copy IProject-specific (but still generic) files
            if (!CopyProjectFiles(project))
            {
                return false;
            }

            // Make changes to the files to tailor them to the specific user input
            if (!SpecializeAppxManifest(project))
            {
                return false;
            }

            var projectWithCustomBuild = project as IProjectWithCustomBuild;
            // Do build step if needed (compiling/generation/etc)
            if (projectWithCustomBuild != null && !BuildProjectAsync(projectWithCustomBuild).Result)
            {
                return false;
            }

            // Add IProject-specific capabilities
            if (!AddCapabilitiesToAppxManifest(project))
            {
                return false;
            }

            // Create mapping file used to build APPX
            var mapFile = outputFolder + @"\main.map.txt";
            if (!CreateAppxMapFile(template, project, mapFile))
            {
                return false;
            }

            // Create APPX file
            var makeAppxResult = CallMakeAppx(makeAppxCmd, mapFile, outputAppx);
            return makeAppxResult;
        }

        private bool GuardTargetName()
        {
            if (string.IsNullOrEmpty(targetName) || targetName.Equals("?"))
            {
                if (targetName.Equals("?"))
                {
                    TelemetryClient.TrackEvent("DeployFromArduinoIde", new Dictionary<string, string>() { });
                }

                // Use last successful deployment target as default
                string initialTargetValue = "";
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\IotCoreAppDeployment"))
                {
                    if (key != null)
                    {
                        initialTargetValue = key.GetValue("Target") as string;
                    }
                }

                // Let user specify target
                TargetNameForm targetNameForm = new TargetNameForm(initialTargetValue);
                var result = targetNameForm.ShowDialog();
                if (result == System.Windows.Forms.DialogResult.OK)
                {
                    targetName = targetNameForm.TargetName;
                }

                if (string.IsNullOrEmpty(targetName))
                {
                    Console.Write(Resource.DeploymentWorker_TargetMissing);
                    return false;
                }
            }

            return true;
        }

        private bool ConvertTargetNameToIp()
        {
            // Is targetName set correctly?
            if (!GuardTargetName())
            {
                return false;
            }

            try
            {
                // Assume an IP address was provided
                IPAddress.Parse(targetName);
            }
            catch (FormatException)
            {
                // If an IP address was not provided, assume device name was provided
                try
                {
                    // Host Name resolution to IP
                    var host = Dns.GetHostEntry(targetName);
                    var ipaddr = host.AddressList.First(ip => ip.AddressFamily == AddressFamily.InterNetwork);
                    if (ipaddr != null)
                    {
                        targetName = ipaddr.ToString();
                    }
                }
                catch (Exception e) when (e is ArgumentNullException || e is ArgumentOutOfRangeException || e is SocketException || e is ArgumentException)
                {
                    Console.Write(string.Format(CultureInfo.InvariantCulture, Resource.DeploymentWorker_DeviceNotFound, targetName, e.Message));
                    return false;
                }
            }

            return true;
        }

        private bool CreateAndDeployApp()
        {
            #region Find Template and Project from available providers

            // Ensure that the target name is converted to IP address as needed
            if (!ConvertTargetNameToIp())
            {
                return false;
            }

            // Ensure that the required Tools (MakeAppx and SignTool) can be found
            var universalSdkRoot = Registry.GetValue(universalSdkRootKey, universalSdkRootValue, null) as string;
            if (universalSdkRoot == null && (makeAppxPath == null || signToolPath == null))
            {
                NotifyThatMakeAppxOrSignToolNotFound();
                return false;
            }

            const string sdkToolCmdFormat = "{0}\\bin\\{1}\\{2}";
            var is64 = Environment.Is64BitOperatingSystem;
            var makeAppxCmd = makeAppxPath ?? string.Format(CultureInfo.InvariantCulture, sdkToolCmdFormat, universalSdkRoot, is64 ? "x64" : "x86", "MakeAppx.exe");
            var signToolCmd = signToolPath ?? string.Format(CultureInfo.InvariantCulture, sdkToolCmdFormat, universalSdkRoot, is64 ? "x64" : "x86", "SignTool.exe");
            if (!File.Exists(makeAppxCmd) || !File.Exists(signToolCmd))
            {
                NotifyThatMakeAppxOrSignToolNotFound();
                return false;
            }

            // Ensure that PowerShell.exe can be found
            var powershellCmd = powershellPath ?? Registry.GetValue(powershellRootKey, powershellRootValue, null) as string;
            if (powershellCmd == null || !File.Exists(powershellCmd))
            {
                OutputMessage(Resource.DeploymentWorker_PowershellNotFound1);
                OutputMessage(Resource.DeploymentWorker_PowershellNotFound2);
                return false;
            }

            // Surround tool cmd paths with quotes in case there are spaces in the paths
            makeAppxCmd = "\"" + makeAppxCmd + "\"";
            signToolCmd = "\"" + signToolCmd + "\"";
            powershellCmd = "\"" + powershellCmd + "\"";

            // Find an appropriate path for the input source
            IProject project = SupportedProjects.FindProject(source);
            if (null == project)
            {
                OutputMessage(string.Format(CultureInfo.InvariantCulture, Resource.DeploymentWorker_NoProjectForSource, source));
                return false;
            }
            OutputMessage(string.Format(CultureInfo.InvariantCulture, Resource.DeploymentWorker_FoundProjectForSource, project.Name));
            TelemetryClient.TrackEvent("DeployProjectType", new Dictionary<string, string>()
                {
                    { "ProjectType", project.Name },
                    { "ProjectTargetArchitecture", targetType.ToString() },
                });

            // Configure IProject with user input
            project.SourceInput = source;
            project.ProcessorArchitecture = targetType;
            project.SdkVersion = sdk;
            project.DependencyConfiguration = configuration;

            // Find base project type ... typically, this is C++ for non-standard UWP
            // project types like Python and Node.js
            var baseProjectType = project.GetBaseProjectType();
            if (IBaseProjectTypes.Other == baseProjectType)
            {
                OutputMessage(string.Format(CultureInfo.InvariantCulture, Resource.DeploymentWorker_NoBaseProjectType, baseProjectType.ToString()));
                return false;
            }

            // Get ITemplate to retrieve shared APPX content
            var template = SupportedProjects.FindTemplate(baseProjectType);
            if (null == template)
            {
                OutputMessage(string.Format(CultureInfo.InvariantCulture, Resource.DeploymentWorker_NoBaseProjectType, baseProjectType.ToString()));
                return false;
            }
            OutputMessage(string.Format(CultureInfo.InvariantCulture, Resource.DeploymentWorker_FoundBaseProjectType, template.Name));

            #endregion

            if (outputFolder == null)
            {
                outputFolder = Path.GetTempPath() + Path.GetRandomFileName();
            }

            var artifactsFolder = outputFolder + @"\output";
            var filename = project.IdentityName + "_" + targetType + "_" + configuration;
            var appxFilename = filename + ".appx";
            var cerFilename = filename + ".cer";
            var outputAppx = artifactsFolder + @"\" + appxFilename;
            var outputCer = artifactsFolder + @"\" + cerFilename;

            if (!Directory.Exists(artifactsFolder))
            {
                Directory.CreateDirectory(artifactsFolder);
            }

            var createResult = CreateAppx(template, project, makeAppxCmd, outputAppx);
            if (!createResult)
            {
                TelemetryClient.TrackEvent("CreateAppxFailure", new Dictionary<string, string>() { });
                return false;
            }

            var pfxFile = outputFolder + @"\TemporaryKey.pfx";
            if (!SignAppx(signToolCmd, outputAppx, pfxFile))
            {
                TelemetryClient.TrackEvent("SignAppxFailure", new Dictionary<string, string>() { });
                return false;
            }

            if (!CreateCertFromPfx(powershellCmd, pfxFile, outputCer))
            {
                TelemetryClient.TrackEvent("CreateCertFailure", new Dictionary<string, string>() { });
                return false;
            }

            var dependencies = project.GetDependencies(SupportedProjects.DependencyProviders);
            if (!CopyDependencyAppxFiles(dependencies, artifactsFolder))
            {
                return false;
            }

            var deployResult = HandleUnauthenticatedDeployAppx(outputAppx, outputCer, dependencies, artifactsFolder, project.IdentityName);
            if (!deployResult)
            {
                TelemetryClient.TrackEvent("WebbDeployFailure", new Dictionary<string, string>() { });
                return deployResult;
            }

            // If app was successfully deployed, cache target in registry
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\IotCoreAppDeployment"))
            {
                key.SetValue("Target", targetName);
            }

            return CopyArtifacts(outputAppx, appxFilename, outputCer, cerFilename, dependencies);
        }

        private DeploymentWorker(Stream outputStream)
        {
            this.outputWriter = new StreamWriter(outputStream) { AutoFlush = true };
            CreateCommandLineParser();
        }

        ~DeploymentWorker()
        {
            #region Cleanup

            if (!keepTempFolder && Directory.Exists(outputFolder))
            {
                Directory.Delete(outputFolder, true);
            }

            #endregion
        }

        private static string getMachineId()
        {
            string id = null;
            try
            {
                // Try querying 64-bit registry for key
                var localRegKey = RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, RegistryView.Registry64);

                if (localRegKey != null)
                {
                    id = (string)localRegKey.OpenSubKey(@"SOFTWARE\Microsoft\SQMClient").GetValue("MachineId");

                    // If can't find key in 64-bit registry, query 32-bit registry
                    if (id == null)
                    {
                        localRegKey = RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, RegistryView.Registry32);

                        if (localRegKey != null)
                        {
                            id = (string)localRegKey.OpenSubKey(@"SOFTWARE\Microsoft\SQMClient").GetValue("MachineId");
                        }
                    }
                }
            }
            catch (Exception)
            {
                // ignored
            }

            if (id != null)
            {
                return id.Replace("{", "").Replace("}", "");
            }

            return null;
        }

        public static bool Execute(string[] args, Stream outputStream)
        {
            var sessionId = Guid.NewGuid().ToString();
            var machineId = getMachineId();
            // Create AppInsights telemetry client to track app usage
            TelemetryClient = new Microsoft.ApplicationInsights.TelemetryClient();
            TelemetryClient.Context.User.Id = machineId;
            TelemetryClient.Context.Session.Id = sessionId;

            Assembly assembly = Assembly.GetAssembly(typeof(DeploymentWorker));
            FileVersionInfo fileVersionInfo = FileVersionInfo.GetVersionInfo(assembly.Location);
            var version = fileVersionInfo.ProductVersion;
            TelemetryClient.TrackEvent("DeployStart", new Dictionary<string, string>()
            {
                { "AppVersion", version }
            });

            var worker = new DeploymentWorker(outputStream);
            if (!worker.argsHandler.HandleCommandLineArgs(args))
            {
                TelemetryClient.TrackEvent("DeployFailed_IncorrectArgs", new Dictionary<string, string>() { });
                TelemetryClient.Flush();
                return false;
            }

            worker.OutputMessage(Resource.DeploymentWorker_Starting);
            var taskResult = worker.CreateAndDeployApp();
            if (taskResult)
            {
                TelemetryClient.TrackEvent("DeploySucceeded", new Dictionary<string, string>() { });
            }
            else
            {
                TelemetryClient.TrackEvent("DeployFailed", new Dictionary<string, string>() { });
            }

            TelemetryClient.Flush();
            return taskResult;
        }
    }
}
