//
// AssemblyResolver.cs
//
// Author:
//   Marek Sieradzki (marek.sieradzki@gmail.com)
//   Ankit Jain (jankit@novell.com)
// 
// (C) 2006 Marek Sieradzki
// Copyright 2009 Novell, Inc (http://www.novell.com)
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

#if NET_2_0

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Microsoft.Build.Tasks {
	internal class AssemblyResolver {

		// name -> (version -> assemblypath)
		Dictionary<string, TargetFrameworkAssemblies> target_framework_cache;
		Dictionary<string, Dictionary<Version, string>> gac;
		TaskLoggingHelper log;
		StringWriter sw;

		public AssemblyResolver ()
		{
			gac = new Dictionary<string, Dictionary<Version, string>> ();
			target_framework_cache = new Dictionary <string, TargetFrameworkAssemblies> ();

			GatherGacAssemblies ();
		}

		public StringWriter SearchLogger {
			get { return sw; }
		}

		public void ResetSearchLogger ()
		{
			sw = new StringWriter ();
		}

		string GetGacPath ()
		{
			// NOTE: code from mcs/tools/gacutil/driver.cs
			PropertyInfo gac = typeof (System.Environment).GetProperty ("GacPath", BindingFlags.Static | BindingFlags.NonPublic);

			if (gac == null)
				return null;

			MethodInfo get_gac = gac.GetGetMethod (true);
			return (string) get_gac.Invoke (null, null);
		}

		void GatherGacAssemblies ()
		{
			string gac_path = GetGacPath ();
			if (gac_path == null)
				throw new InvalidOperationException ("XBuild must be run on Mono runtime");
			if (!Directory.Exists (gac_path))
				return; // in case mono isn't "installed".

			Version version;
			DirectoryInfo version_info, assembly_info;

			foreach (string assembly_name in Directory.GetDirectories (gac_path)) {
				assembly_info = new DirectoryInfo (assembly_name);
				foreach (string version_token in Directory.GetDirectories (assembly_name)) {
					foreach (string file in Directory.GetFiles (version_token, "*.dll")) {
						version_info = new DirectoryInfo (version_token);
						version = new Version (version_info.Name.Split (
							new char [] {'_'}, StringSplitOptions.RemoveEmptyEntries) [0]);

						Dictionary<Version, string> assembliesByVersion = new Dictionary <Version, string> ();
						if (!gac.TryGetValue (assembly_info.Name, out assembliesByVersion)) {
							assembliesByVersion = new Dictionary <Version, string> ();
							gac.Add (assembly_info.Name, assembliesByVersion);
						}

						string found_file;
						if (assembliesByVersion.TryGetValue (version, out found_file) &&
							File.GetLastWriteTime (file) <= File.GetLastWriteTime (found_file))
								// Duplicate found, take the newer file
								continue;

						assembliesByVersion [version] = file;
					}
				}
			}
		}

		public ResolvedReference FindInTargetFramework (ITaskItem reference, string framework_dir, bool specific_version)
		{
			AssemblyName key_aname = new AssemblyName (reference.ItemSpec);
			TargetFrameworkAssemblies gac_asm;
			if (!target_framework_cache.TryGetValue (framework_dir, out gac_asm)) {
				// fill gac_asm
				gac_asm = target_framework_cache [framework_dir] = PopulateTargetFrameworkAssemblies (framework_dir);
			}

			KeyValuePair<AssemblyName, string> pair;
			if (gac_asm.NameToAssemblyNameCache.TryGetValue (key_aname.Name, out pair)) {
				if (AssemblyNamesCompatible (key_aname, pair.Key, specific_version)) {
					// gac and tgt frmwk refs are not copied private
					return GetResolvedReference (reference, pair.Value, pair.Key, false,
							SearchPath.TargetFrameworkDirectory);
				}

				SearchLogger.WriteLine ("Considered target framework dir {0}, assembly name '{1}' did not " +
						"match the expected '{2}' (SpecificVersion={3})",
						framework_dir, pair.Key, key_aname, specific_version);
			} else {
				SearchLogger.WriteLine ("Considered target framework dir {0}, assembly named '{1}' not found.",
						framework_dir, key_aname.Name);
			}
			return null;
		}

		public ResolvedReference FindInDirectory (ITaskItem reference, string directory, string [] file_extensions)
		{
			if (reference.ItemSpec.IndexOf (',') < 0) {
				// Try as a filename
				string path = Path.Combine (directory, reference.ItemSpec);
				AssemblyName aname = GetAssemblyNameFromFile (path);
				if (aname != null)
					return GetResolvedReference (reference, path, aname, true, SearchPath.Directory);

				foreach (string extn in file_extensions) {
					string path_with_extn = path + extn;
					aname = GetAssemblyNameFromFile (path_with_extn);
					if (aname != null)
						return GetResolvedReference (reference, path_with_extn, aname, true,
								SearchPath.Directory);
				}
			}

			// Probably an assembly name
			AssemblyName key_aname = new AssemblyName (reference.ItemSpec);
			foreach (string extn in file_extensions) {
				foreach (string file in Directory.GetFiles (directory, "*" + extn)) {
					AssemblyName found_aname = GetAssemblyNameFromFile (file);
					if (found_aname == null)
						// error already logged
						continue;

					//FIXME: Extract 'name' and look only for name.dll name.exe ?
					if (AssemblyNamesCompatible (key_aname, found_aname, false))
						return GetResolvedReference (reference, file, found_aname, true,
								SearchPath.Directory);

					SearchLogger.WriteLine ("Considered {0}, but assembly name wasn't compatible.", file);
				}
			}

			return null;
		}

		TargetFrameworkAssemblies PopulateTargetFrameworkAssemblies (string directory)
		{
			TargetFrameworkAssemblies gac_asm = new TargetFrameworkAssemblies (directory);
			foreach (string file in Directory.GetFiles (directory, "*.dll")) {
				AssemblyName aname = GetAssemblyNameFromFile (file);
				gac_asm.NameToAssemblyNameCache [aname.Name] =
					new KeyValuePair<AssemblyName, string> (aname, file);
			}

			return gac_asm;
		}

		public ResolvedReference ResolveGacReference (ITaskItem reference, bool specific_version)
		{
			AssemblyName name = new AssemblyName (reference.ItemSpec);
			if (!gac.ContainsKey (name.Name)) {
				SearchLogger.WriteLine ("Considered {0}, but could not find in the GAC.",
						reference.ItemSpec);
				return null;
			}

			if (name.Version != null) {
				string ret;
				if (gac [name.Name].TryGetValue (name.Version, out ret))
					return GetResolvedReference (reference, ret, name, false, SearchPath.Gac);

				// not found
				if (specific_version) {
					SearchLogger.WriteLine ("Considered '{0}', but an assembly with the specific version not found.",
							reference.ItemSpec);
					return null;
				}
			}

			Version [] versions = new Version [gac [name.Name].Keys.Count];
			gac [name.Name].Keys.CopyTo (versions, 0);
			Array.Sort (versions, (IComparer <Version>) null);
			Version highest = versions [versions.Length - 1];
			//FIXME: the aname being used here isn't correct, its version should
			//	 actually match "highest"
			return GetResolvedReference (reference, gac [name.Name] [highest], name, false, SearchPath.Gac);
		}

		public ResolvedReference ResolveHintPathReference (ITaskItem reference, bool specific_version)
		{
			AssemblyName name = new AssemblyName (reference.ItemSpec);
			ResolvedReference resolved = null;

			string hintpath = reference.GetMetadata ("HintPath");
			if (String.IsNullOrEmpty (hintpath)) {
				SearchLogger.WriteLine ("HintPath attribute not found");
				return null;
			}

			if (!File.Exists (hintpath)) {
				log.LogMessage (MessageImportance.Low, "HintPath {0} does not exist.", hintpath);
				SearchLogger.WriteLine ("Considered {0}, but it does not exist.", hintpath);
				return null;
			}

			AssemblyName found = GetAssemblyNameFromFile (hintpath);
			if (found == null) {
				log.LogMessage (MessageImportance.Low, "File at HintPath {0}, is either an invalid assembly or the file does not exist.", hintpath);
				return null;
			}

			if (AssemblyNamesCompatible (name, found, specific_version)) {
				resolved = GetResolvedReference (reference, hintpath, found, true, SearchPath.HintPath);
			} else {
				SearchLogger.WriteLine ("Considered {0}, but assembly name '{1}' did not match the " +
						"expected '{2}' (SpecificVersion={3})", hintpath, found, name, specific_version);
				log.LogMessage (MessageImportance.Low, "Assembly names are not compatible.");
			}

			return resolved;
		}

		public AssemblyName GetAssemblyNameFromFile (string filename)
		{
			AssemblyName aname = null;
			filename = Path.GetFullPath (filename);
			try {
				aname = AssemblyName.GetAssemblyName (filename);
			} catch (FileNotFoundException) {
				SearchLogger.WriteLine ("Considered '{0}' as a file, but the file does not exist",
						filename);
			} catch (BadImageFormatException) {
				SearchLogger.WriteLine ("Considered '{0}' as a file, but it is an invalid assembly",
						filename);
			}

			return aname;
		}

		internal static bool AssemblyNamesCompatible (AssemblyName a, AssemblyName b, bool specificVersion)
		{
			if (a.Name != b.Name)
				return false;

			if (a.CultureInfo != null && !a.CultureInfo.Equals (b.CultureInfo))
				return false;

			if (specificVersion && a.Version != null && a.Version != b.Version)
				return false;

			byte [] a_bytes = a.GetPublicKeyToken ();
			byte [] b_bytes = b.GetPublicKeyToken ();

			if (specificVersion) {
				bool a_is_empty = (a_bytes == null || a_bytes.Length == 0);
				bool b_is_empty = (b_bytes == null || b_bytes.Length == 0);

				if (a_is_empty && b_is_empty)
					return true;

				if (a_is_empty || b_is_empty)
					return false;

				for (int i = 0; i < a_bytes.Length; i++)
					if (a_bytes [i] != b_bytes [i])
						return false;
			}

			return true;
		}

		public bool IsStrongNamed (AssemblyName name)
		{
			return (name.Version != null &&
					name.GetPublicKeyToken () != null &&
					name.GetPublicKeyToken ().Length != 0);
		}

		// FIXME: to get default values of CopyLocal, compare with TargetFrameworkDirectories

		// If metadata 'Private' is present then use that or use @default_value
		// as the value for CopyLocal
		internal ResolvedReference GetResolvedReference (ITaskItem reference, string filename,
				AssemblyName aname, bool default_value, SearchPath search_path)
		{
			string pvt = reference.GetMetadata ("Private");

			bool copy_local = default_value;
			if (!String.IsNullOrEmpty (pvt))
				//FIXME: log a warning for invalid value
				Boolean.TryParse (pvt, out copy_local);

			return new ResolvedReference (filename, aname, copy_local, search_path);
		}

		public TaskLoggingHelper Log {
			set { log = value; }
		}
	}

	class TargetFrameworkAssemblies {
		public string Path;

		// assembly (simple) name -> (AssemblyName, file path)
		public Dictionary <string, KeyValuePair<AssemblyName, string>> NameToAssemblyNameCache;

		public TargetFrameworkAssemblies (string path)
		{
			this.Path = path;
			NameToAssemblyNameCache = new Dictionary<string, KeyValuePair<AssemblyName, string>> ();
		}
	}

	enum SearchPath
	{
		Gac,
		TargetFrameworkDirectory,
		CandidateAssemblies,
		HintPath,
		Directory,
		RawFileName
	}
}



#endif
