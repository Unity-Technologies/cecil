using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using NUnit.Framework;

namespace Mono.Cecil.Tests {

	[TestFixture]
	public class LazyInitializationTests : BaseTestFixture {

		const int Rounds = 200;
		const int Threads = 8;

		[Test]
		public void AssemblyResolverCreatedOnceByConcurrentCallers ()
		{
			for (int round = 0; round < Rounds; round++) {
				var module = CreateModule ();
				var resolvers = RunConcurrently (() => module.AssemblyResolver);

				Assert.AreEqual (1, resolvers.Distinct ().Count ());
			}
		}

		[Test]
		public void CoreLibraryReferenceAddedOnceByConcurrentCallers ()
		{
			for (int round = 0; round < Rounds; round++) {
				var module = CreateModule ();
				var common = (TypeSystem) module.TypeSystem;
				var references = RunConcurrently (() => common.CoreLibrary);

				Assert.AreEqual (1, references.Distinct ().Count ());
				Assert.AreEqual (1, module.AssemblyReferences.Count (r => r.Name == "mscorlib"));
			}
		}

		static ModuleDefinition CreateModule ()
		{
			return ModuleDefinition.CreateModule ("lazy", ModuleKind.Dll);
		}

		static List<T> RunConcurrently<T> (Func<T> action)
		{
			var results = new T [Threads];
			var errors = new Exception [Threads];
			var barrier = new Barrier (Threads);
			var threads = new Thread [Threads];

			for (int i = 0; i < Threads; i++) {
				var index = i;
				threads [i] = new Thread (() => {
					try {
						Assert.IsTrue (barrier.SignalAndWait (TimeSpan.FromSeconds (30)));
						results [index] = action ();
					} catch (Exception e) {
						errors [index] = e;
					}
				}) { IsBackground = true };
				threads [i].Start ();
			}

			foreach (var thread in threads)
				Assert.IsTrue (thread.Join (TimeSpan.FromSeconds (30)));

			foreach (var error in errors)
				Assert.IsNull (error);

			return results.ToList ();
		}
	}
}
