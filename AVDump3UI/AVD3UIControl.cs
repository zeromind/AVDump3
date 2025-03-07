﻿using AVDump3Lib;
using AVDump3Lib.Information.InfoProvider;
using AVDump3Lib.Information.MetaInfo;
using AVDump3Lib.Information.MetaInfo.Core;
using AVDump3Lib.Misc;
using AVDump3Lib.Modules;
using AVDump3Lib.Processing;
using AVDump3Lib.Processing.BlockConsumers;
using AVDump3Lib.Processing.BlockConsumers.Matroska;
using AVDump3Lib.Processing.BlockConsumers.MP4;
using AVDump3Lib.Processing.BlockConsumers.Ogg;
using AVDump3Lib.Processing.FileMove;
using AVDump3Lib.Processing.HashAlgorithms;
using AVDump3Lib.Processing.StreamConsumer;
using AVDump3Lib.Processing.StreamProvider;
using AVDump3Lib.Reporting.Core;
using AVDump3Lib.Settings;
using AVDump3Lib.Settings.Core;
using ExtKnot.StringInvariants;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace AVDump3UI;

public class AVD3UIControlFileProcessedEventArgs : EventArgs {
	public FileMetaInfo FileMetaInfo { get; }

	private readonly List<Task<bool>> processingTasks = new();
	private readonly Dictionary<string, string> fileMoveTokens = new();

	public IEnumerable<Task<bool>> ProcessingTasks => processingTasks;
	public IReadOnlyDictionary<string, string> FileMoveTokens => fileMoveTokens;

	public void AddProcessingTask(Task<bool> processingTask) => processingTasks.Add(processingTask);

	public void AddFileMoveToken(string key, string value) => fileMoveTokens.Add(key, value);

	public AVD3UIControlFileProcessedEventArgs(FileMetaInfo fileMetaInfo) => FileMetaInfo = fileMetaInfo;
}


public interface IAVD3UIControl : IAVD3Module {
	event EventHandler<AVD3UIControlExceptionEventArgs> ExceptionThrown;
	event EventHandler<AVD3UIControlFileProcessedEventArgs> FileProcessed;
	event EventHandler ProcessingFinished;

	IAVD3Console Console { get; }

	void RegisterShutdownDelay(WaitHandle waitHandle);
	void WriteLine(string value);
}


public interface IAVD3Console {
	void WriteLine(IEnumerable<string> values);
	void WriteLine(string value);
}

public class AVD3UIControlExceptionEventArgs : EventArgs {
	public AVD3UIControlExceptionEventArgs(XElement exception) {
		Exception = exception;
	}

	public XElement Exception { get; private set; }
}


public class AVD3UIControl : IAVD3UIControl, IFileMoveConfigure {
	public event EventHandler<AVD3UIControlExceptionEventArgs>? ExceptionThrown = delegate { };
	public event EventHandler<AVD3UIControlFileProcessedEventArgs>? FileProcessed = delegate { };
	public event EventHandler ProcessingFinished = delegate { };
	public ImmutableArray<IBlockConsumerFactory> BlockConsumerFactories { get; private set; }
	public IReadOnlyCollection<IReportFactory> ReportFactories { get; }

	public event EventHandler<BlockConsumerFilterEventArgs> BlockConsumerFilter = delegate { };
	public event EventHandler<FilePathFilterEventArgs> FilePathFilter = delegate { };

	public IAVD3Console Console { get; }

	private IFileMoveScript fileMove;
	private HashSet<string> filePathsToSkip = new();
	//private IServiceProvider fileMoveServiceProvider;
	//private ScriptRunner<string> fileMoveScriptRunner;

	private AVD3UISettings settings;
	private readonly object fileSystemLock = new();
	private readonly List<WaitHandle> shutdownDelayHandles = new();
	private readonly AVD3ModuleManagement moduleManagement = new();

	public AVD3UIControl(IAVD3Console console) {
		//AppDomain.CurrentDomain.UnhandledException += UnhandleException;
		Console = console ?? throw new ArgumentNullException(nameof(console));

		moduleManagement.LoadModules(AppDomain.CurrentDomain.BaseDirectory ?? throw new Exception("AppDomain.CurrentDomain.BaseDirectory is null"));
		moduleManagement.AddModule(this);

		moduleManagement.RaiseIntialize();

	}

	private static class NativeMethods {
		[DllImport("AVDump3NativeLib")]
		internal static extern CPUInstructions RetrieveCPUInstructions();
	}


	public IReadOnlyList<ISettingProperty> SettingProperties => settingProperties;
	private readonly List<ISettingProperty> settingProperties = new();
	public IReadOnlyCollection<IInfoProviderFactory> InfoProviderFactories { get; }


	public CPUInstructions AvailableSIMD { get; } = NativeMethods.RetrieveCPUInstructions();

	public void RegisterDefaultBlockConsumers(IDictionary<string, ImmutableArray<string>> arguments) {
		var factories = new Dictionary<string, IBlockConsumerFactory>();
		void addOrReplace(IBlockConsumerFactory factory) => factories[factory.Name] = factory;
		string? getArgumentAt(BlockConsumerSetup s, int index, string? defVal) {
			if(arguments == null) return defVal;
			return arguments.TryGetValue(s.Name, out var args) && index < args.Length ? args[index] ?? defVal : defVal;
		}


		addOrReplace(new BlockConsumerFactory("NULL", s => new HashCalculator(s.Name, s.Reader, new NullHashAlgorithm(getArgumentAt(s, 0, "4").ToInvInt32() << 20))));
		addOrReplace(new BlockConsumerFactory("CPY", s => new CopyToFileBlockConsumer(s.Name, s.Reader, Path.Combine(getArgumentAt(s, 0, null) ?? throw new Exception(), Path.GetFileName((string)s.Tag)))));
		addOrReplace(new BlockConsumerFactory("MD5", s => new HashCalculator(s.Name, s.Reader, new AVDHashAlgorithmIncrmentalHashAdapter(HashAlgorithmName.MD5, 1024))));
		addOrReplace(new BlockConsumerFactory("SHA1", s => new HashCalculator(s.Name, s.Reader, new AVDHashAlgorithmIncrmentalHashAdapter(HashAlgorithmName.SHA1, 1024))));
		addOrReplace(new BlockConsumerFactory("SHA2-256", s => new HashCalculator(s.Name, s.Reader, new AVDHashAlgorithmIncrmentalHashAdapter(HashAlgorithmName.SHA256, 1024))));
		addOrReplace(new BlockConsumerFactory("SHA2-384", s => new HashCalculator(s.Name, s.Reader, new AVDHashAlgorithmIncrmentalHashAdapter(HashAlgorithmName.SHA384, 1024))));
		addOrReplace(new BlockConsumerFactory("SHA2-512", s => new HashCalculator(s.Name, s.Reader, new AVDHashAlgorithmIncrmentalHashAdapter(HashAlgorithmName.SHA512, 1024))));
		addOrReplace(new BlockConsumerFactory("MD4", s => new HashCalculator(s.Name, s.Reader, new Md4HashAlgorithm())));
		addOrReplace(new BlockConsumerFactory("ED2K", s => new HashCalculator(s.Name, s.Reader, new Ed2kHashAlgorithm())));
		addOrReplace(new BlockConsumerFactory("CRC32", s => new HashCalculator(s.Name, s.Reader, new Crc32HashAlgorithm())));
		addOrReplace(new BlockConsumerFactory("MKV", s => new MatroskaParser(s.Name, s.Reader)));
		addOrReplace(new BlockConsumerFactory("OGG", s => new OggParser(s.Name, s.Reader)));
		addOrReplace(new BlockConsumerFactory("MP4", s => new MP4Parser(s.Name, s.Reader)));


		try {
			addOrReplace(new BlockConsumerFactory("ED2K", s => new HashCalculator(s.Name, s.Reader, new Ed2kNativeHashAlgorithm())));
			addOrReplace(new BlockConsumerFactory("MD4", s => new HashCalculator(s.Name, s.Reader, new Md4NativeHashAlgorithm())));
			addOrReplace(new BlockConsumerFactory("CRC32", s => new HashCalculator(s.Name, s.Reader, new Crc32NativeHashAlgorithm())));
			addOrReplace(new BlockConsumerFactory("SHA3-224", s => new HashCalculator(s.Name, s.Reader, new SHA3NativeHashAlgorithm(224))));
			addOrReplace(new BlockConsumerFactory("SHA3-256", s => new HashCalculator(s.Name, s.Reader, new SHA3NativeHashAlgorithm(256))));
			addOrReplace(new BlockConsumerFactory("SHA3-384", s => new HashCalculator(s.Name, s.Reader, new SHA3NativeHashAlgorithm(384))));
			addOrReplace(new BlockConsumerFactory("SHA3-512", s => new HashCalculator(s.Name, s.Reader, new SHA3NativeHashAlgorithm(512))));
			addOrReplace(new BlockConsumerFactory("KECCAK-224", s => new HashCalculator(s.Name, s.Reader, new KeccakNativeHashAlgorithm(224))));
			addOrReplace(new BlockConsumerFactory("KECCAK-256", s => new HashCalculator(s.Name, s.Reader, new KeccakNativeHashAlgorithm(256))));
			addOrReplace(new BlockConsumerFactory("KECCAK-384", s => new HashCalculator(s.Name, s.Reader, new KeccakNativeHashAlgorithm(384))));
			addOrReplace(new BlockConsumerFactory("KECCAK-512", s => new HashCalculator(s.Name, s.Reader, new KeccakNativeHashAlgorithm(512))));

			if(AvailableSIMD.HasFlag(CPUInstructions.SSE2)) {
				addOrReplace(new BlockConsumerFactory("TIGER", s => new HashCalculator(s.Name, s.Reader, new TigerNativeHashAlgorithm())));
				addOrReplace(new BlockConsumerFactory("TTH", s => new HashCalculator(s.Name, s.Reader, new TigerTreeHashAlgorithm(getArgumentAt(s, 0, Math.Min(4, Environment.ProcessorCount).ToInvString()).ToInvInt32()))));
				addOrReplace(new BlockConsumerFactory("CRC32", s => new HashCalculator(s.Name, s.Reader, new Crc32NativeHashAlgorithm())));
			}
			if(AvailableSIMD.HasFlag(CPUInstructions.SSE42)) {
				addOrReplace(new BlockConsumerFactory("CRC32C", s => new HashCalculator(s.Name, s.Reader, new Crc32CIntelHashAlgorithm())));
			}
			if(AvailableSIMD.HasFlag(CPUInstructions.SHA) && false) { //Broken (Produces wrong hashes)
				addOrReplace(new BlockConsumerFactory("SHA1", s => new HashCalculator(s.Name, s.Reader, new SHA1NativeHashAlgorithm())));
				addOrReplace(new BlockConsumerFactory("SHA2-256", s => new HashCalculator(s.Name, s.Reader, new SHA256NativeHashAlgorithm())));
			}


		} catch(Exception) {
			//TODO Log
		}

		var blockConsumerFactories = factories.Values.ToList();
		blockConsumerFactories.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
		BlockConsumerFactories = ImmutableArray.CreateRange(blockConsumerFactories);
	}
	public void RegisterSettings(IEnumerable<ISettingProperty> settingsGroups) => settingProperties.AddRange(settingsGroups);

	//private void UnhandleException(object sender, UnhandledExceptionEventArgs e) {
	//	var wrapEx = new AVD3CLException(
	//		"Unhandled AppDomain wide Exception",
	//		e.ExceptionObject as Exception ?? new Exception("Non Exception Type: " + e.ExceptionObject.ToString())
	//	);

	//	OnException(wrapEx);
	//}

	private void OnException(AVD3CLException ex) {
		var exElem = ex.ToXElement(
			settings?.Diagnostics.SkipEnvironmentElement ?? false,
			settings?.Diagnostics.IncludePersonalData ?? false
		);

		if(settings?.Diagnostics.SaveErrors ?? false) {
			lock(fileSystemLock) {
				Directory.CreateDirectory(settings.Diagnostics.ErrorDirectory);
				var filePath = Path.Combine(settings.Diagnostics.ErrorDirectory, "AVD3Error" + ex.ThrownOn.ToString("yyyyMMdd HHmmssffff") + ".xml");

				using var fileStream = File.OpenWrite(filePath);
				using var xmlWriter = System.Xml.XmlWriter.Create(fileStream, new System.Xml.XmlWriterSettings { CheckCharacters = false, Encoding = Encoding.UTF8, Indent = true });
				exElem.WriteTo(xmlWriter);
			}
		}

		var exception = ex.GetBaseException() ?? ex;

		Console.WriteLine("Error " + exception.GetType() + ": " + exception.Message);

		ExceptionThrown?.Invoke(this, new AVD3UIControlExceptionEventArgs(exElem));
		//TODO Raise Event for modules to listen to
	}

	public void Initialize() {
		BlockConsumerFilter += (s, e) => {
			if(settings?.Processing.Consumers.Value.Any(x => e.BlockConsumerName.InvEqualsOrdCI(x.Name)) ?? false) {
				e.Accept();
			}
		};

		FilePathFilter += (s, e) => {
			if(settings == null) throw new InvalidOperationException("Called FilePathFilter when settings is null");

			var accept = settings.FileDiscovery.WithExtensions.Allow == (
				settings.FileDiscovery.WithExtensions.Items.Length == 0 ||
				settings.FileDiscovery.WithExtensions.Items.Any(
					fe => e.FilePath.EndsWith(fe, StringComparison.InvariantCultureIgnoreCase)
				)
			) && !filePathsToSkip.Contains(e.FilePath);
			if(!accept) e.Decline();
		};

		RegisterSettings(AVD3UISettings.GetProperties());
	}
	void IAVD3Module.Initialize(IReadOnlyCollection<IAVD3Module> modules) => Initialize();

	public ModuleInitResult Initialized() => new(false);
	public void Shutdown() { }

	public void ConfigurationFinished(object? sender, SettingsModuleInitResult args) {
		settings = new AVD3UISettings(args.Store);

		//progressDisplay = new AVD3ProgressDisplay(settings.Display);
		//console.WriteProgress += progressDisplay.WriteProgress;

		RegisterDefaultBlockConsumers((settings.Processing.Consumers ?? ImmutableArray<ConsumerSettings>.Empty).ToDictionary(x => x.Name, x => x.Arguments));

		if(settings.Processing.Consumers == null) {
			System.Console.WriteLine("Available Consumers: ");
			foreach(var blockConsumerFactory in BlockConsumerFactories) {
				System.Console.WriteLine(blockConsumerFactory.Name.PadRight(14) + " - " + blockConsumerFactory.Description);
			}
			args.Cancel();

		} else if(settings.Processing.Consumers.Value.Any()) {
			var invalidBlockConsumerNames = settings.Processing.Consumers.Value.Where(x => BlockConsumerFactories.All(y => !y.Name.InvEqualsOrdCI(x.Name))).ToArray();
			if(invalidBlockConsumerNames.Any()) {
				System.Console.WriteLine("Invalid BlockConsumer(s): " + string.Join(", ", invalidBlockConsumerNames.Select(x => x.Name)));
				args.Cancel();
			}
		}


		if(settings.Reporting.Reports == null) {
			System.Console.WriteLine("Available Reports: ");
			foreach(var reportFactory in ReportFactories) {
				System.Console.WriteLine(reportFactory.Name.PadRight(14) + " - " + reportFactory.Description);
			}
			args.Cancel();
			return;

		} else if(settings.Reporting.Reports?.Any() ?? false) {
			var invalidReportNames = settings.Reporting.Reports.Value.Where(x => ReportFactories.All(y => !y.Name.InvEqualsOrdCI(x))).ToArray();
			if(invalidReportNames.Any()) {
				System.Console.WriteLine("Invalid Report: " + string.Join(", ", invalidReportNames));
				args.Cancel();
			}
		}

		if(!string.IsNullOrEmpty(settings.Reporting.CRC32Error?.Path)) {
			BlockConsumerFilter += (s, e) => {
				if(e.BlockConsumerName.InvEqualsOrd("CRC32")) e.Accept();
			};
		}

		if(settings.Processing.PrintAvailableSIMDs) {
			System.Console.WriteLine("Available SIMD Instructions: ");
			foreach(var flagValue in Enum.GetValues(typeof(CPUInstructions)).OfType<CPUInstructions>().Where(x => (x & AvailableSIMD) != 0)) {
				System.Console.WriteLine(flagValue);
			}
			args.Cancel();
		}

		var invalidFilePaths = settings.FileDiscovery.SkipLogPath.Where(p => !File.Exists(p));
		if(!invalidFilePaths.Any()) {
			filePathsToSkip = new HashSet<string>(settings.FileDiscovery.SkipLogPath.SelectMany(p => File.ReadLines(p)));

		} else if(settings.FileDiscovery.SkipLogPath.Any()) {
			System.Console.WriteLine("SkipLogPath contains file paths which do not exist: " + string.Join(", ", invalidFilePaths));
			args.Cancel();
		}

		static void CreateDirectoryChain(string? path, bool isDirectory = false) {
			if(!isDirectory) path = Path.GetDirectoryName(path);
			if(!string.IsNullOrEmpty(path)) Directory.CreateDirectory(path);
		}

		if(settings.Diagnostics.NullStreamTest != null && settings.Reporting.Reports.Value.Length > 0) {
			System.Console.WriteLine("NullStreamTest cannot be used with reports");
			args.Cancel();
		}

		if(settings.FileMove.Mode != FileMoveMode.None) {
			var fileMoveExtensions = moduleManagement.OfType<IFileMoveConfigure>().ToArray();

			static string PlaceholderConvert(string pattern) => "return \"" + Regex.Replace(pattern.Replace("\\", "\\\\").Replace("\"", "\\\""), @"\$\{([A-Za-z0-9\-\.]+)\}", @""" + Get(""$1"") + """) + "\";";

			fileMove = settings.FileMove.Mode switch {
				FileMoveMode.PlaceholderInline => new FileMoveScriptByInlineScript(fileMoveExtensions, PlaceholderConvert(settings.FileMove.Pattern)),
				FileMoveMode.CSharpScriptInline => new FileMoveScriptByInlineScript(fileMoveExtensions, settings.FileMove.Pattern),
				FileMoveMode.PlaceholderFile => new FileMoveScriptByScriptFile(fileMoveExtensions, settings.FileMove.Pattern, x => PlaceholderConvert(x)),
				FileMoveMode.CSharpScriptFile => new FileMoveScriptByScriptFile(fileMoveExtensions, settings.FileMove.Pattern),
				FileMoveMode.DotNetAssembly => new FileMoveScriptByAssembly(fileMoveExtensions, settings.FileMove.Pattern),
				_ => throw new NotImplementedException()
			};

			if(!settings.FileMove.Test) {
				fileMove.Load();

			} else {
				if(!fileMove.CanReload) {
					System.Console.WriteLine("FileMove cannot enter test mode because the choosen --FileMove.Mode cannot be reloaded. It needs to be file based!");
					args.Cancel();
				}
			}
		}

		foreach(var processedLogPath in settings.FileDiscovery.ProcessedLogPath) CreateDirectoryChain(processedLogPath);
		foreach(var skipLogPath in settings.FileDiscovery.SkipLogPath) CreateDirectoryChain(skipLogPath);
		CreateDirectoryChain(settings.Reporting.CRC32Error?.Path);
		CreateDirectoryChain(settings.Reporting.ExtensionDifferencePath);
		CreateDirectoryChain(settings.Reporting.ReportDirectory, true);
		CreateDirectoryChain(settings.Diagnostics.ErrorDirectory, true);

	}


	public NullStreamProvider CreateNullStreamProvider() {
		if(settings.Diagnostics.NullStreamTest == null) throw new AVD3CLException("Called CreateNullStreamProvider where Diagnostics.NullStreamTest was null");

		var nsp = new NullStreamProvider(
			settings.Diagnostics.NullStreamTest.StreamCount,
			settings.Diagnostics.NullStreamTest.StreamLength,
			settings.Diagnostics.NullStreamTest.ParallelStreamCount
		);

		//progressDisplay.TotalFiles = nsp.StreamCount;
		//progressDisplay.TotalBytes = nsp.StreamCount * nsp.StreamLength;

		return nsp;
	}

	public void Process(string[] paths) {
		throw new NotImplementedException();
		//var bytesReadProgress = new BytesReadProgress(processingModule.BlockConsumerFactories.Select(x => x.Name));

		//var sp = settings.Diagnostics.NullStreamTest != null ? CreateNullStreamProvider() : CreateFileStreamProvider(paths);
		//var streamConsumerCollection = CreateStreamConsumerCollection(sp,
		//	settings.Processing.BufferLength,
		//	settings.Processing.ProducerMinReadLength,
		//	settings.Processing.ProducerMaxReadLength
		//);
		//streamConsumerCollection.ConsumingStream += ConsumingStream;

		//using(console)
		//using(sp as IDisposable)
		//using(var cts = new CancellationTokenSource()) {
		//	//progressDisplay.Initialize(bytesReadProgress.GetProgress);


		//	if(!settings.Display.ForwardConsoleCursorOnly) console.StartProgressDisplay();

		//	void cancelKeyHandler(object s, ConsoleCancelEventArgs e) {
		//		System.Console.CancelKeyPress -= cancelKeyHandler;
		//		e.Cancel = true;
		//		cts.Cancel();
		//	}
		//	System.Console.CancelKeyPress += cancelKeyHandler;
		//	System.Console.CursorVisible = false;
		//	try {
		//		streamConsumerCollection.ConsumeStreams(bytesReadProgress, cts.Token);
		//		if(console.ShowingProgress) console.StopProgressDisplay();

		//		ProcessingFinished?.Invoke(this, EventArgs.Empty);

		//		var shutdownDelayHandles = this.shutdownDelayHandles.ToArray();
		//		if(shutdownDelayHandles.Length > 0) WaitHandle.WaitAll(shutdownDelayHandles);

		//	} catch(OperationCanceledException) {

		//	} finally {
		//		if(console.ShowingProgress) console.StopProgressDisplay();
		//		System.Console.CursorVisible = true;
		//	}
		//}

		//if(settings.Processing.PauseBeforeExit) {
		//	System.Console.WriteLine("Program execution has finished. Press any key to exit.");
		//	System.Console.Read();
		//}
	}

	private IStreamProvider CreateFileStreamProvider(string[] paths) {
		throw new NotImplementedException();

		//var acceptedFiles = 0;
		//var fileDiscoveryOn = DateTimeOffset.UtcNow;
		//var sp = (StreamFromPathsProvider)processingModule.CreateFileStreamProvider(
		//	paths, settings.FileDiscovery.Recursive, settings.FileDiscovery.Concurrent,
		//	path => {
		//		if(fileDiscoveryOn.AddSeconds(1) < DateTimeOffset.UtcNow) {
		//			System.Console.WriteLine("Accepted files: " + acceptedFiles);
		//			fileDiscoveryOn = DateTimeOffset.UtcNow;
		//		}
		//		acceptedFiles++;
		//	},
		//	ex => {
		//		if(!(ex is UnauthorizedAccessException) || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
		//			System.Console.WriteLine("Filediscovery: " + ex.Message);
		//		}
		//	}
		//);
		//System.Console.WriteLine("Accepted files: " + acceptedFiles);
		//System.Console.WriteLine();

		////progressDisplay.TotalFiles = sp.TotalFileCount;
		////progressDisplay.TotalBytes = sp.TotalBytes;

		//return sp;

	}

	private async void ConsumingStream(object? sender, ConsumingStreamEventArgs e) {
		var filePath = (string)e.Tag;
		var fileName = Path.GetFileName(filePath);

		var hasProcessingError = false;
		e.OnException += (s, args) => {
			args.IsHandled = true;
			args.Retry = args.RetryCount < 2;
			hasProcessingError = !args.IsHandled;

			OnException(new AVD3CLException("ConsumingStream", args.Cause) { Data = { { "FileName", new SensitiveData(fileName) } } });
		};


		try {
			var blockConsumers = await e.FinishedProcessing.ConfigureAwait(false);
			if(hasProcessingError) return;


			var fileMetaInfo = CreateFileMetaInfo(filePath, blockConsumers);
			if(fileMetaInfo != null) {
				var success = true;
				success = success && await HandleReporting(fileMetaInfo);
				success = success && await HandleEvent(fileMetaInfo);
				success = success && await HandleFileMove(fileMetaInfo);

				if(settings.FileDiscovery.ProcessedLogPath.Any() && success) {
					lock(settings.FileDiscovery) {
						foreach(var processedLogPath in settings.FileDiscovery.ProcessedLogPath) {
							File.AppendAllText(processedLogPath, fileMetaInfo.FileInfo.FullName + "\n");
						}
					}
				}
			}

		} finally {
			e.ResumeNext.Set();
		}
	}

	private FileMetaInfo? CreateFileMetaInfo(string filePath, ImmutableArray<IBlockConsumer> blockConsumers) {
		var fileName = Path.GetFileName(filePath);

		try {
			var infoSetup = new InfoProviderSetup(filePath, blockConsumers);
			var infoProviders = InfoProviderFactories.Select(x => x.Create(infoSetup)).ToArray();
			return new FileMetaInfo(new FileInfo(filePath), infoProviders);

		} catch(Exception ex) {
			OnException(new AVD3CLException("CreatingInfoProviders", ex) { Data = { { "FileName", new SensitiveData(fileName) } } });
			return null;
		}
	}

	private async Task<bool> HandleReporting(FileMetaInfo fileMetaInfo) {
		await Task.Yield();

		var fileName = Path.GetFileName(fileMetaInfo.FileInfo.FullName);


		var linesToWrite = new List<string>(32);
		if(settings.Reporting.PrintHashes || settings.Reporting.PrintReports) {
			linesToWrite.Add(fileName);
		}

		if(settings.Reporting.PrintHashes) {
			foreach(var item in fileMetaInfo.Providers.OfType<HashProvider>().FirstOrDefault().Items.OfType<MetaInfoItem<ImmutableArray<byte>>>()) {
				linesToWrite.Add(item.Type.Key + " => " + BitConverter.ToString(item.Value.ToArray()).Replace("-", ""));
			}
			linesToWrite.Add("");
		}
		if(!string.IsNullOrEmpty(settings.Reporting.CRC32Error?.Path)) {
			var hashProvider = fileMetaInfo.CondensedProviders.Where(x => x.Type == HashProvider.HashProviderType).Single();
			var metaInfoItem = hashProvider.Items.FirstOrDefault(x => x.Type.Key.Equals("CRC32"));

			if(metaInfoItem != null) {
				var crc32Hash = (ImmutableArray<byte>)metaInfoItem.Value;
				var crc32HashStr = BitConverter.ToString(crc32Hash.ToArray(), 0).Replace("-", "");

				if(!Regex.IsMatch(fileMetaInfo.FileInfo.FullName, settings.Reporting.CRC32Error?.Pattern.Replace("${CRC32}", crc32HashStr))) {
					lock(settings.Reporting) {
						File.AppendAllText(
							settings.Reporting.CRC32Error?.Path,
							crc32HashStr + " " + fileMetaInfo.FileInfo.FullName
						);
					}
				}
			}
		}

		if(!string.IsNullOrEmpty(settings.Reporting.ExtensionDifferencePath)) {
			var metaDataProvider = fileMetaInfo.CondensedProviders.Where(x => x.Type == MediaProvider.MediaProviderType).Single();
			var detExts = metaDataProvider.Select(MediaProvider.SuggestedFileExtensionType)?.Value ?? ImmutableArray.Create<string>();
			var ext = fileMetaInfo.FileInfo.Extension.StartsWith('.') ? fileMetaInfo.FileInfo.Extension[1..] : fileMetaInfo.FileInfo.Extension;

			if(!detExts.Contains(ext, StringComparer.OrdinalIgnoreCase)) {
				if(detExts.Length == 0) detExts = ImmutableArray.Create("unknown");

				lock(settings.Reporting) {
					File.AppendAllText(
						settings.Reporting.ExtensionDifferencePath,
						ext + " => " + string.Join(" ", detExts) + "\t" + fileMetaInfo.FileInfo.FullName
					);
				}
			}
		}

		var success = true;
		var reportsFactories = ReportFactories.Where(x => settings.Reporting.Reports?.Any(y => x.Name.Equals(y, StringComparison.OrdinalIgnoreCase)) ?? false).ToArray();
		if(reportsFactories.Length != 0) {

			try {

				var reportItems = reportsFactories.Select(x => new { x.Name, Report = x.Create(fileMetaInfo) });

				foreach(var reportItem in reportItems) {
					if(settings.Reporting.PrintReports) {
						linesToWrite.Add(reportItem.Report.ReportToString(Utils.UTF8EncodingNoBOM) + "\n");
					}

					var reportFileName = settings.Reporting.ReportFileName;
					reportFileName = reportFileName.Replace("${FileName}", fileName);
					reportFileName = reportFileName.Replace("${FileNameWithoutExtension}", Path.GetFileNameWithoutExtension(fileName));
					reportFileName = reportFileName.Replace("${FileExtension}", Path.GetExtension(fileName).Replace(".", ""));
					reportFileName = reportFileName.Replace("${ReportName}", reportItem.Name);
					reportFileName = reportFileName.Replace("${ReportFileExtension}", reportItem.Report.FileExtension);

					lock(fileSystemLock) {
						reportItem.Report.SaveToFile(Path.Combine(settings.Reporting.ReportDirectory, reportFileName), "", Utils.UTF8EncodingNoBOM);
					}
				}

			} catch(Exception ex) {
				OnException(new AVD3CLException("GeneratingReports", ex) { Data = { { "FileName", new SensitiveData(fileName) } } });
				success = false;
			}
		}
		Console.WriteLine(linesToWrite);
		return success;
	}
	private async Task<bool> HandleEvent(FileMetaInfo fileMetaInfo) {
		var success = true;

		var fileProcessedEventArgs = new AVD3UIControlFileProcessedEventArgs(fileMetaInfo);
		try {
			FileProcessed?.Invoke(this, fileProcessedEventArgs);
		} catch(Exception ex) {
			OnException(new AVD3CLException("FileProcessedEvent", ex) { Data = { { "FilePath", new SensitiveData(fileMetaInfo.FileInfo.FullName) } } });
			success = false;
		}
		success &= (await Task.WhenAll(fileProcessedEventArgs.ProcessingTasks).ConfigureAwait(false)).All(x => x);

		return success;
	}
	private async Task<bool> HandleFileMove(FileMetaInfo fileMetaInfo) {
		var success = true;
		if(fileMove != null) {
			//using var clLock = settings.FileMove.Test ? Console.LockConsole() : null;

			try {
				var moveFile = true;
				var actionKey = ' ';
				var repeat = settings.FileMove.Test;
				do {

					string? destFilePath = null;
					try {
						if(settings.FileMove.Test) fileMove.Load();

						destFilePath = await fileMove.GetFilePathAsync(fileMetaInfo);

						if(settings.FileMove.DisableFileMove) {
							destFilePath = Path.Combine(Path.GetDirectoryName(fileMetaInfo.FileInfo.FullName) ?? "", Path.GetFileName(destFilePath));
						}
						if(settings.FileMove.DisableFileRename) {
							destFilePath = Path.Combine(Path.GetDirectoryName(destFilePath) ?? "", Path.GetFileName(fileMetaInfo.FileInfo.FullName));
						}

					} catch(Exception) {
						destFilePath = null;
					}


					if(settings.FileMove.Test) {
						System.Console.WriteLine();
						System.Console.WriteLine();
						System.Console.WriteLine("FileMove.Test Enabled" + (settings.FileMove.DisableFileMove ? " (DisableFileMove Enabled!)" : "") + (settings.FileMove.DisableFileRename ? " (DisableFileRename Enabled!)" : ""));
						System.Console.WriteLine("Directoryname: ");
						System.Console.WriteLine("Old: " + fileMetaInfo.FileInfo.DirectoryName);
						System.Console.WriteLine("New: " + Path.GetDirectoryName(destFilePath));
						System.Console.WriteLine("Filename: ");
						System.Console.WriteLine("Old: " + fileMetaInfo.FileInfo.Name);
						System.Console.WriteLine("New: " + Path.GetFileName(destFilePath));


						if(actionKey == 'A') {
							System.Console.WriteLine("Press any key to cancel automatic mode");


							while(!System.Console.KeyAvailable && !fileMove.SourceChanged()) {
								await Task.Delay(500);
							}

							if(System.Console.KeyAvailable) {
								actionKey = ' ';
							} else {
								continue;
							}
						}

						do {
							System.Console.WriteLine();
							System.Console.WriteLine("How do you wish to continue?");
							System.Console.WriteLine("(C) Continue without moving the file");
							System.Console.WriteLine("(R) Repeat script execution");
							System.Console.WriteLine("(A) Repeat script execution automatically on sourcefile change");
							System.Console.WriteLine("(M) Moving the file and continue");
							System.Console.Write("User Input: ");

							while(System.Console.KeyAvailable) System.Console.ReadKey(true);
							actionKey = char.ToUpperInvariant(System.Console.ReadKey().KeyChar);

							if(actionKey == -1) actionKey = 'C';
							System.Console.WriteLine();
							System.Console.WriteLine();

						} while(actionKey != 'C' && actionKey != 'R' && actionKey != 'A' && actionKey != 'M');

						moveFile = actionKey == 'M';
						repeat = actionKey == 'R' || actionKey == 'A';
					}

					if(moveFile && !string.IsNullOrEmpty(destFilePath) && !string.Equals(destFilePath, fileMetaInfo.FileInfo.FullName, StringComparison.Ordinal)) {
						await Task.Run(() => {
							var originalPath = fileMetaInfo.FileInfo.FullName;
							fileMetaInfo.FileInfo.MoveTo(destFilePath);

							if(!string.IsNullOrEmpty(settings.FileMove.LogPath)) {
								lock(fileSystemLock) {
									File.AppendAllText(settings.FileMove.LogPath, originalPath + " => " + destFilePath);
								}
							}
						}).ConfigureAwait(false);
					}

				} while(repeat);

			} catch(Exception) {
				success = false;
			}

		}

		return success;
	}

	public void WriteLine(string value) => Console.WriteLine(value);


	public void RegisterShutdownDelay(WaitHandle waitHandle) => shutdownDelayHandles.Add(waitHandle);


	void IFileMoveConfigure.ConfigureServiceCollection(IServiceCollection services) { }
	string IFileMoveConfigure.ReplaceToken(string key, FileMoveContext ctx) {
		var fileMetaInfo = ctx.FileMetaInfo;

		var value = key switch {
			"FullName" => fileMetaInfo.FileInfo.FullName,
			"FileName" => fileMetaInfo.FileInfo.Name,
			"FileExtension" => fileMetaInfo.FileInfo.Extension,
			"FileNameWithoutExtension" => Path.GetFileNameWithoutExtension(fileMetaInfo.FileInfo.FullName),
			"DirectoryName" => fileMetaInfo.FileInfo.DirectoryName,
			_ => null,
		};

		if(key.StartsWith("SuggestedExtension")) {
			var metaDataProvider = fileMetaInfo.CondensedProviders.FirstOrDefault(x => x.Type == MediaProvider.MediaProviderType);
			var detExts = metaDataProvider.Select(MediaProvider.SuggestedFileExtensionType)?.Value ?? ImmutableArray.Create<string>();
			value = detExts.FirstOrDefault()?.Transform(x => "." + x) ?? fileMetaInfo.FileInfo.Extension;
		}

		if(key.StartsWith("Hash")) {
			var m = Regex.Match(key, @"Hash-(?<Name>[^-]+)-(?<Base>\d+)-(?<Case>UC|LC|OC)");
			if(m.Success) {
				var hashName = m.Groups["Name"].Value;
				var withBase = m.Groups["Base"].Value;
				var letterCase = m.Groups["Case"].Value;

				var hashData = fileMetaInfo.CondensedProviders.FirstOrDefault(x => x.Type == HashProvider.HashProviderType)?.Select<HashInfoItemType, ImmutableArray<byte>>(hashName)?.Value.ToArray();
				if(hashData != null) value = BitConverterEx.ToBase(hashData, BitConverterEx.Bases[withBase]).Transform(x => letterCase switch { "UC" => x.ToInvUpper(), "LC" => x.ToInvLower(), "OC" => x, _ => x });
			}
		}
		return value;
	}

}

public class AVD3CLException : AVD3LibException {
	public AVD3CLException(string message, Exception innerException) : base(message, innerException) { }
	public AVD3CLException(string message) : base(message) { }
}
