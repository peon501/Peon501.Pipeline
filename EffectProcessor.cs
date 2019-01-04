using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Content.Pipeline;
using Microsoft.Xna.Framework.Content.Pipeline.Graphics;
using Microsoft.Xna.Framework.Content.Pipeline.Processors;
using System.Threading.Tasks;
using System.Runtime.Serialization.Json;
using System.IO;
using Microsoft.Xna.Framework.Content.Pipeline.Serialization.Compiler;
using Microsoft.Xna.Framework.Content;
using System.Text;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using Newtonsoft.Json;

namespace Peon501.Pipeline.Processors
{
	[ContentProcessor (DisplayName = "Effect Processor - Matas Lešinskas")]
	public sealed class LocalEffectProcessor : EffectProcessor
	{
        [DefaultValue ("/.wine/drive_c/Program Files (x86)/MSBuild/MonoGame/v3.0/Tools/2MGFX.exe")]
        public string CompilerPath { get; set; }
        
        [DefaultValue ("/opt/wine-staging/bin/wine64")]
        public string WinePath { get; set; }
        
		public LocalEffectProcessor ()
		{
			CompilerPath  = "/.wine/drive_c/Program Files (x86)/MSBuild/MonoGame/v3.0/Tools/2MGFX.exe";
			WinePath  = "/opt/wine-staging/bin/wine64";
		}

		void ReadInclude (StringBuilder sb, string filename)
		{
			foreach (var line in File.ReadAllLines (filename)) {
				if (line.StartsWith ("//"))
					continue;
				if (line.StartsWith ("#include", StringComparison.InvariantCultureIgnoreCase)) {
					var root = Path.GetDirectoryName (filename);
					var startIndex = line.IndexOf ("\"") + 1;
					var file = line.Substring (startIndex, line.IndexOf ("\"", startIndex) - startIndex);
					ReadInclude (sb, Path.Combine (root, file));
				}
				else {
					sb.AppendLine (line.Trim ());
				}
			}
		}

		string ResolveCode (EffectContent input)
		{
			StringBuilder sb = new StringBuilder ();
			foreach (var line in input.EffectCode.Split (new char [] { '\n' })) {
				if (line.StartsWith ("//", StringComparison.InvariantCultureIgnoreCase))
					continue;
				if (line.StartsWith ("#include", StringComparison.InvariantCultureIgnoreCase)) {
					// read the file
					var startIndex = line.IndexOf ("\"", StringComparison.InvariantCultureIgnoreCase) + 1;
					var file = line.Substring (startIndex, line.IndexOf ("\"", startIndex, StringComparison.InvariantCultureIgnoreCase) - startIndex);
					var root = Path.GetDirectoryName (input.Identity.SourceFilename);
					ReadInclude (sb, Path.Combine (root, file));
				}
				else {
					sb.AppendLine (line.Trim ());
				}
			}
			return sb.ToString ();
		}

		public override CompiledEffectContent Process (EffectContent input, ContentProcessorContext context)
		{
			if (Environment.OSVersion.Platform != PlatformID.Unix) {
				return base.Process (input, context);
			}
			var code = ResolveCode (input);
			var platform = context.TargetPlatform;
			var version = typeof (EffectContent).Assembly.GetName ().Version;

			string error = String.Empty;
			byte[] buf = Mgfx.RunMGCB(code, platform.ToString(), WinePath, CompilerPath, out error);
			var resultSer = JsonConvert.SerializeObject (new Result() { Compiled = buf, Error = error });
			var result = JsonDeSerializer (resultSer);

			if (!string.IsNullOrEmpty (result.Error)) {
				throw new Exception (result.Error);
			}
			if (result.Compiled == null || result.Compiled.Length == 0)
				throw new Exception ("There was an error compiling the effect");
				
			return new CompiledEffectContent (result.Compiled);
			return null;
		}

		public string JsonSerializer(Data objectToSerialize) 
		{ 
			return Newtonsoft.Json.JsonConvert.SerializeObject (objectToSerialize, Newtonsoft.Json.Formatting.None);
		} 
		public Result JsonDeSerializer(string data) 
		{ 
			return (Result)Newtonsoft.Json.JsonConvert.DeserializeObject (data, typeof(Result));
		} 
	}
	public class Data {
			public string Platform { get; set; }
			public string Code { get; set; }
			public string Version { get; set; }
	}
	public class Result
	{
		public byte[] Compiled { get; set;  }
		public string Error { get; set;  }
	}
	static class Mgfx
	{
		public static byte[] RunMGCB(string code,  string platform, string winePath, string compilerPath, out string error)
		{

			string[] platforms = new string[]
			{
				"DesktopGL",
				"Android",
				"iOS",
				"tvOS",
				"OUYA",
			};
			var profile = platforms.Contains(platform) ? "OpenGL" : "DirectX_11";
			var tempPath = Path.GetFileName( Path.ChangeExtension(Path.GetTempFileName (), ".fx"));
			var xnb = Path.ChangeExtension (tempPath, ".mgfx");
			var tempOutput = Path.GetTempPath ();
			File.WriteAllText(Path.Combine(tempOutput, tempPath), code);
			
			error = String.Empty;

			string homeDir = Environment.GetEnvironmentVariable("HOME");
			string programPath = $"{homeDir}{compilerPath}";
			string effectDir = $"/tmp/{tempPath}";
			string mgfxDir = $"/tmp/{xnb}";

			effectDir = effectDir.Replace("/", "\\");
			mgfxDir = mgfxDir.Replace("/", "\\");

			string parameters = string.Format("{0} \"{1}\" \"{2}\" \"{3}\" /Profile:{4}", winePath, programPath, effectDir, mgfxDir, profile);

			parameters = parameters.Replace("\"", "\'");
			Process proc = new Process();
			proc.StartInfo.FileName = "/bin/bash";
			proc.StartInfo.Arguments = string.Format("-c \"{0}\"", parameters);
			proc.StartInfo.UseShellExecute = false;
			proc.StartInfo.RedirectStandardOutput = true;
			proc.StartInfo.RedirectStandardInput = true;
			proc.StartInfo.RedirectStandardError = true;

			var stdoutCompleted = new ManualResetEvent(false);
			proc.Start();
			
			var response = new System.Text.StringBuilder();
			while (!proc.StandardOutput.EndOfStream){
				response.AppendLine(proc.StandardOutput.ReadLine());
			}
			if (response.ToString().Contains("Compiled")){
				stdoutCompleted.Set();
			}else{
				error = proc.StandardError.ReadLine();
				throw new Exception (error);
			}

			try {
				proc.WaitForExit ();
				if (File.Exists (Path.Combine (tempOutput, xnb))) {
					return File.ReadAllBytes (Path.Combine(tempOutput, xnb));
				}
			} catch (Exception ex) {
				error = ex.ToString ();
				throw new Exception (error);
			}
			finally {
				File.Delete (Path.Combine(tempOutput, tempPath));
				File.Delete (Path.Combine(tempOutput, xnb));
			}
			if (proc.ExitCode != 0)
			{
				throw new InvalidContentException();
			}
			return new byte[0];

		}
	}
}


