using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

using Mono.Cecil.Cil;

using NUnit.Framework;

namespace Mono.Cecil.Tests {

	// Each test builds two modules whose metadata can only be decoded with a type from the other module, then
	// immediately reads both of them from two threads. The assembly resolver holds the first resolution on each thread
	// until both threads have made one. If a thread still holds its own module's lock at that point, both threads then
	// block on each other's lock, and the test fails on its timeout instead of hanging.
	[TestFixture]
	public class ModuleLockTests : BaseTestFixture {

		static readonly TimeSpan RendezvousTimeout = TimeSpan.FromSeconds (5);
		static readonly TimeSpan DeadlockTimeout = TimeSpan.FromSeconds (30);

		enum CrossReference {
			CustomAttributeEnumArgument,
			SecurityAttributeEnumProperty,
			FieldRVAValueType,
		}

		[Test]
		public void ImmediateReadWithCustomAttributeEnumArgumentsFromEachOther ()
		{
			ImmediateReadFromTwoThreads (CrossReference.CustomAttributeEnumArgument, (holder, other) => {
				var argument = holder.CustomAttributes [0].ConstructorArguments [0];

				Assert.AreEqual (other + ".Enum", argument.Type.FullName);
				Assert.AreEqual ((short) 7, argument.Value);
			});
		}

		[Test]
		public void ImmediateReadWithSecurityAttributeEnumPropertiesFromEachOther ()
		{
			ImmediateReadFromTwoThreads (CrossReference.SecurityAttributeEnumProperty, (holder, other) => {
				var property = holder.SecurityDeclarations [0].SecurityAttributes [0].Properties [0];

				Assert.AreEqual ("Value", property.Name);
				Assert.AreEqual (other + ".Enum", property.Argument.Type.FullName);
				Assert.AreEqual ((short) 7, property.Argument.Value);
			});
		}

		[Test]
		public void ImmediateReadWithFieldRVAValueTypesFromEachOther ()
		{
			ImmediateReadFromTwoThreads (CrossReference.FieldRVAValueType, (holder, other) => {
				var field = holder.Fields [0];

				Assert.AreEqual (other + ".Blob", field.FieldType.FullName);
				Assert.AreEqual (new byte [] { 1, 2, 3, 4, 5, 6, 7, 8 }, field.InitialValue);
			});
		}

		static void ImmediateReadFromTwoThreads (CrossReference kind, Action<TypeDefinition, string> check)
		{
			var resolver = new RendezvousAssemblyResolver (2);
			var parameters = new ReaderParameters (ReadingMode.Deferred) { AssemblyResolver = resolver };

			var a = AssemblyDefinition.ReadAssembly (new MemoryStream (BuildModule ("DeadlockA", "DeadlockB", kind)), parameters);
			var b = AssemblyDefinition.ReadAssembly (new MemoryStream (BuildModule ("DeadlockB", "DeadlockA", kind)), parameters);
			resolver.Add (a);
			resolver.Add (b);

			var errors = new Exception [2];
			var threads = new [] {
				StartImmediateRead (a.MainModule, resolver, errors, 0),
				StartImmediateRead (b.MainModule, resolver, errors, 1),
			};

			// A deadlocked thread never finishes. Both are background threads, so they do not keep the test run alive.
			var deadline = DateTime.UtcNow + DeadlockTimeout;
			foreach (var thread in threads) {
				var remaining = deadline - DateTime.UtcNow;
				if (!thread.Join (remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero))
					Assert.Fail ("ImmediateRead of '{0}' did not finish within {1} s; the module locks deadlocked", thread.Name, DeadlockTimeout.TotalSeconds);
			}

			for (int i = 0; i < errors.Length; i++)
				if (errors [i] != null)
					throw new Exception ("ImmediateRead failed on " + threads [i].Name, errors [i]);

			check (a.MainModule.GetType ("DeadlockA", "Holder"), "DeadlockB");
			check (b.MainModule.GetType ("DeadlockB", "Holder"), "DeadlockA");
		}

		static Thread StartImmediateRead (ModuleDefinition module, RendezvousAssemblyResolver resolver, Exception [] errors, int index)
		{
			var thread = new Thread (() => {
				try {
					resolver.JoinRendezvous ();
					module.ImmediateRead ();
				} catch (Exception e) {
					errors [index] = e;
				}
			});

			thread.IsBackground = true;
			thread.Name = module.Assembly.Name.Name;
			thread.Start ();
			return thread;
		}

		static byte [] BuildModule (string name, string other, CrossReference kind)
		{
			var module = ModuleDefinition.CreateModule (name, ModuleKind.Dll);
			var core_library = module.TypeSystem.CoreLibrary;
			var other_assembly = new AssemblyNameReference (other, new Version (1, 0, 0, 0));
			module.AssemblyReferences.Add (other_assembly);

			// The holder comes first, so the other module's thread reaches its own holder before it has read the Enum
			// and Blob types that this module's holder needs.
			var holder = new TypeDefinition (name, "Holder", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, module.TypeSystem.Object);
			module.Types.Add (holder);

			switch (kind) {
			case CrossReference.CustomAttributeEnumArgument: {
				var attribute = new TypeDefinition (name, "CrossAttribute", TypeAttributes.Public, new TypeReference ("System", "Attribute", module, core_library));
				var constructor = new MethodDefinition (".ctor", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, module.TypeSystem.Void);
				constructor.Parameters.Add (new ParameterDefinition ("value", ParameterAttributes.None, new TypeReference (other, "Enum", module, other_assembly, true)));
				constructor.Body.Instructions.Add (Instruction.Create (OpCodes.Ret));
				attribute.Methods.Add (constructor);
				module.Types.Add (attribute);

				// Prolog, the Int16 value 7 of the other module's enum, no named arguments
				holder.CustomAttributes.Add (new CustomAttribute (constructor, new byte [] { 0x01, 0x00, 0x07, 0x00, 0x00, 0x00 }));
				break;
			}
			case CrossReference.SecurityAttributeEnumProperty:
				holder.Attributes |= TypeAttributes.HasSecurity;
				holder.SecurityDeclarations.Add (new SecurityDeclaration (SecurityAction.Demand, SecurityDeclarationBlob (name, other)));
				break;
			case CrossReference.FieldRVAValueType:
				holder.Fields.Add (new FieldDefinition ("Data", FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.InitOnly, new TypeReference (other, "Blob", module, other_assembly, true)) {
					InitialValue = new byte [] { 1, 2, 3, 4, 5, 6, 7, 8 },
				});
				break;
			}

			var @enum = new TypeDefinition (name, "Enum", TypeAttributes.Public | TypeAttributes.Sealed, new TypeReference ("System", "Enum", module, core_library));
			@enum.Fields.Add (new FieldDefinition ("value__", FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName, module.TypeSystem.Int16));
			module.Types.Add (@enum);

			var blob = new TypeDefinition (name, "Blob", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.ExplicitLayout, new TypeReference ("System", "ValueType", module, core_library)) {
				ClassSize = 8,
				PackingSize = 1,
			};
			module.Types.Add (blob);

			var stream = new MemoryStream ();
			module.Write (stream);
			return stream.ToArray ();
		}

		// One security attribute whose only named argument is the property (0x54) Value, set to the Int16 value 7 of the other
		// module's enum (0x55)
		static byte [] SecurityDeclarationBlob (string name, string other)
		{
			var arguments = new MemoryStream ();
			arguments.WriteByte (1);
			arguments.WriteByte (0x54);
			arguments.WriteByte (0x55);
			WriteSerString (arguments, other + ".Enum, " + other);
			WriteSerString (arguments, "Value");
			arguments.WriteByte (7);
			arguments.WriteByte (0);

			var blob = new MemoryStream ();
			blob.WriteByte ((byte) '.');
			blob.WriteByte (1);
			WriteSerString (blob, name + ".CrossAttribute, " + name);
			blob.WriteByte ((byte) arguments.Length);
			arguments.WriteTo (blob);
			return blob.ToArray ();
		}

		// The strings here are shorter than 0x80 bytes, so their compressed length is a single byte
		static void WriteSerString (Stream stream, string value)
		{
			var bytes = Encoding.UTF8.GetBytes (value);
			stream.WriteByte ((byte) bytes.Length);
			stream.Write (bytes, 0, bytes.Length);
		}

		sealed class RendezvousAssemblyResolver : IAssemblyResolver {

			readonly Dictionary<string, AssemblyDefinition> assemblies = new Dictionary<string, AssemblyDefinition> ();
			readonly ThreadLocal<bool> waiting = new ThreadLocal<bool> ();
			readonly ManualResetEventSlim all_arrived = new ManualResetEventSlim ();
			int remaining;

			public RendezvousAssemblyResolver (int threads)
			{
				remaining = threads;
			}

			public void Add (AssemblyDefinition assembly)
			{
				assemblies.Add (assembly.Name.Name, assembly);
			}

			// The calling thread waits for the others in its first resolution
			public void JoinRendezvous ()
			{
				waiting.Value = true;
			}

			public AssemblyDefinition Resolve (AssemblyNameReference name)
			{
				return Resolve (name, new ReaderParameters ());
			}

			public AssemblyDefinition Resolve (AssemblyNameReference name, ReaderParameters parameters)
			{
				if (waiting.Value) {
					waiting.Value = false;
					if (Interlocked.Decrement (ref remaining) == 0)
						all_arrived.Set ();

					// Only a fix that serializes the two reads keeps the other thread away; it then continues alone
					all_arrived.Wait (RendezvousTimeout);
				}

				AssemblyDefinition assembly;
				if (!assemblies.TryGetValue (name.Name, out assembly))
					throw new AssemblyResolutionException (name);

				return assembly;
			}

			public void Dispose ()
			{
			}
		}
	}
}
