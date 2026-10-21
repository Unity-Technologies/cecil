//
// Author:
//   Jb Evain (jbevain@gmail.com)
//
// Copyright (c) 2008 - 2015 Jb Evain
// Copyright (c) 2008 - 2011 Novell, Inc.
//
// Licensed under the MIT/X11 license.
//

using System;
using System.Collections.Generic;

namespace Mono.Cecil {

	public class DefaultAssemblyResolver : BaseAssemblyResolver {

		readonly IDictionary<string, AssemblyDefinition> cache;

		public DefaultAssemblyResolver ()
		{
			cache = new Dictionary<string, AssemblyDefinition> (StringComparer.Ordinal);
		}

		public override AssemblyDefinition Resolve (AssemblyNameReference name)
		{
			Mixin.CheckName (name);

			AssemblyDefinition assembly;
			lock (cache) {
				if (cache.TryGetValue (name.FullName, out assembly))
					return assembly;
			}

			// Not under the lock: a subclass or a ResolveFailure handler can resolve other assemblies while this one is read,
			// and concurrent misses of one name each read it, the first one stored is the one that is kept
			assembly = base.Resolve (name);

			lock (cache) {
				AssemblyDefinition cached;
				if (cache.TryGetValue (name.FullName, out cached) && cached != assembly) {
					// Another thread resolved it first
					if (assembly != null)
						assembly.Dispose ();

					return cached;
				}

				cache [name.FullName] = assembly;
			}

			return assembly;
		}

		protected void RegisterAssembly (AssemblyDefinition assembly)
		{
			if (assembly == null)
				throw new ArgumentNullException ("assembly");

			var name = assembly.Name.FullName;
			lock (cache) {
				if (cache.ContainsKey (name))
					return;

				cache [name] = assembly;
			}
		}

		protected override void Dispose (bool disposing)
		{
			lock (cache) {
				foreach (var assembly in cache.Values)
					assembly.Dispose ();

				cache.Clear ();
			}

			base.Dispose (disposing);
		}
	}
}
