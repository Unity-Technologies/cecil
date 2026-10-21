using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

using Mono.Cecil.Cil;
using Mono.Collections.Generic;

using NUnit.Framework;

namespace Mono.Cecil.Tests {

	// Tests for decoding blobs without holding the module lock: that concurrent decodes of one module agree with a serial
	// read, and that a failed decode followed by a successful one behaves as it did when the decoder held the lock.
	[TestFixture]
	public class ModuleLockStressTests : BaseTestFixture {

		// Many threads decode the same module's custom attributes and security declarations, with every argument shape
		// that resolves a type (enums from another module and from this one, nested enums, boxed enums, enum arrays,
		// System.Type values, named fields and properties), while other threads do locked lazy reads and ImmediateRead
		// of the same module. Every thread must see what a single-threaded read sees.
		[Test]
		public void ConcurrentReadsOfOneModuleMatchASerialRead ()
		{
			var b_bytes = BuildStressB ();
			var a_bytes = BuildStressA (b_bytes, 24);

			var serial = ReadPair (a_bytes, b_bytes);
			var expected_attributes = RenderAttributes (serial [0].MainModule, null);
			var expected_security = RenderSecurity (serial [0].MainModule, null);
			var expected_lazy = RenderLazy (serial [0].MainModule, null);

			const int threads = 12;
			for (int iteration = 0; iteration < 15; iteration++) {
				var pair = ReadPair (a_bytes, b_bytes);
				var module = pair [0].MainModule;
				var start = new Barrier (threads);
				var errors = new List<string> ();
				var workers = new Thread [threads];
				for (int t = 0; t < threads; t++) {
					int id = t;
					int seed = iteration * 100 + t;
					workers [t] = new Thread (() => {
						var random = new Random (seed);
						try {
							start.SignalAndWait ();
							switch (id % 4) {
							case 0:
							case 1:
								Compare (errors, "attributes", expected_attributes, RenderAttributes (module, random));
								break;
							case 2:
								Compare (errors, "security", expected_security, RenderSecurity (module, random));
								Compare (errors, "lazy", expected_lazy, RenderLazy (module, random));
								break;
							case 3:
								module.ImmediateRead ();
								Compare (errors, "attributes", expected_attributes, RenderAttributes (module, random));
								break;
							}
						} catch (Exception e) {
							lock (errors)
								errors.Add (e.ToString ());
						}
					});
					workers [t].IsBackground = true;
					workers [t].Start ();
				}

				foreach (var worker in workers)
					Assert.IsTrue (worker.Join (TimeSpan.FromSeconds (30)), "a reader did not finish");

				Compare (errors, "attributes after", expected_attributes, RenderAttributes (module, null));
				Compare (errors, "security after", expected_security, RenderSecurity (module, null));
				Assert.IsEmpty (errors, string.Join (Environment.NewLine, errors.Take (5)));
			}
		}

		// Two threads decoding attributes of one module that use the same forwarded enum must not see a circularity.
		[Test]
		public void ConcurrentDecodesOfOneModuleThroughOneForwarder ()
		{
			var target = AssemblyDefinition.CreateAssembly (new AssemblyNameDefinition ("FwdTarget", new Version (1, 0, 0, 0)), "FwdTarget", ModuleKind.Dll);
			target.MainModule.Types.Add (CreateEnum (target.MainModule, "FwdTarget", "Enum", target.MainModule.TypeSystem.Int16));

			var forwarder = AssemblyDefinition.CreateAssembly (new AssemblyNameDefinition ("Fwd", new Version (1, 0, 0, 0)), "Fwd", ModuleKind.Dll);
			var target_reference = new AssemblyNameReference ("FwdTarget", new Version (1, 0, 0, 0));
			forwarder.MainModule.AssemblyReferences.Add (target_reference);
			forwarder.MainModule.ExportedTypes.Add (new ExportedType ("FwdTarget", "Enum", forwarder.MainModule, target_reference) { IsForwarder = true });

			var user = AssemblyDefinition.CreateAssembly (new AssemblyNameDefinition ("FwdUser", new Version (1, 0, 0, 0)), "FwdUser", ModuleKind.Dll);
			var user_module = user.MainModule;
			var forwarder_reference = new AssemblyNameReference ("Fwd", new Version (1, 0, 0, 0));
			user_module.AssemblyReferences.Add (forwarder_reference);
			var attribute = new TypeDefinition ("FwdUser", "UseAttribute", TypeAttributes.Public, new TypeReference ("System", "Attribute", user_module, user_module.TypeSystem.CoreLibrary));
			var constructor = new MethodDefinition (".ctor", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, user_module.TypeSystem.Void);
			constructor.Parameters.Add (new ParameterDefinition ("value", ParameterAttributes.None, new TypeReference ("FwdTarget", "Enum", user_module, forwarder_reference, true)));
			constructor.Body.Instructions.Add (Instruction.Create (OpCodes.Ret));
			attribute.Methods.Add (constructor);
			user_module.Types.Add (attribute);
			for (int i = 0; i < 2; i++) {
				var holder = new TypeDefinition ("FwdUser", "Holder" + i, TypeAttributes.Public, user_module.TypeSystem.Object);
				holder.CustomAttributes.Add (new CustomAttribute (constructor, new byte [] { 0x01, 0x00, (byte) (i + 1), 0x00, 0x00, 0x00 }));
				user_module.Types.Add (holder);
			}

			var first_inside = new ManualResetEventSlim ();
			var release_first = new ManualResetEventSlim ();
			var resolver = new HookResolver ();
			var parameters = new ReaderParameters (ReadingMode.Deferred) { AssemblyResolver = resolver };
			resolver.Add (AssemblyDefinition.ReadAssembly (new MemoryStream (WriteToBytes (target)), parameters));
			resolver.Add (AssemblyDefinition.ReadAssembly (new MemoryStream (WriteToBytes (forwarder)), parameters));
			var read_user = AssemblyDefinition.ReadAssembly (new MemoryStream (WriteToBytes (user)), parameters);
			var first_thread = 0;
			var second_inside = new ManualResetEventSlim ();
			resolver.Hook = name => {
				if (name.Name != "FwdTarget")
					return;

				// The first thread waits inside the forwarded resolution, holding the forwarder's guard
				if (Thread.CurrentThread.ManagedThreadId == first_thread) {
					first_inside.Set ();
					release_first.Wait (TimeSpan.FromSeconds (60));
				} else {
					second_inside.Set ();
				}
			};

			var holder0 = read_user.MainModule.GetType ("FwdUser.Holder0").CustomAttributes [0];
			var holder1 = read_user.MainModule.GetType ("FwdUser.Holder1").CustomAttributes [0];

			Exception first_error = null, second_error = null;
			object first_value = null, second_value = null;
			var first = new Thread (() => {
				try { first_value = holder0.ConstructorArguments [0].Value; } catch (Exception e) { first_error = e; }
			}) { IsBackground = true };
			first_thread = first.ManagedThreadId;
			first.Start ();
			Assert.IsTrue (first_inside.Wait (TimeSpan.FromSeconds (10)), "the first decode did not reach the forwarded resolution");

			var second = new Thread (() => {
				try { second_value = holder1.ConstructorArguments [0].Value; } catch (Exception e) { second_error = e; }
			}) { IsBackground = true };
			second.Start ();
			// The second decode must get inside the forwarded resolution too while the first one is still in it, which it cannot
			// if it fails on the guard of the first one. In that case its thread ends, so stop waiting then.
			var wait_until = DateTime.UtcNow + TimeSpan.FromSeconds (10);
			var second_reached = false;
			while (!(second_reached = second_inside.Wait (10)) && second.IsAlive && DateTime.UtcNow < wait_until) {
			}

			release_first.Set ();
			Assert.IsTrue (first.Join (TimeSpan.FromSeconds (30)));
			Assert.IsTrue (second.Join (TimeSpan.FromSeconds (30)));

			Assert.IsNull (first_error, "first: " + first_error);
			Assert.IsNull (second_error, "second: " + second_error);
			Assert.IsTrue (second_reached, "the second decode did not run inside the forwarded resolution while the first one was in it");
			Assert.AreEqual ((short) 1, first_value);
			Assert.AreEqual ((short) 2, second_value);
		}

		// The collections that Fields/Properties hand out while an attribute cannot be decoded are the ones that later receive
		// the decoded arguments.
		[Test]
		public void CollectionsHandedOutWhileUnresolvableReceiveTheArgumentsLater ()
		{
			var b_bytes = BuildStressB ();
			var b_resolver = new HookResolver ();
			var b = AssemblyDefinition.ReadAssembly (new MemoryStream (b_bytes), new ReaderParameters { AssemblyResolver = b_resolver });
			b_resolver.Add (b);

			var a = AssemblyDefinition.CreateAssembly (new AssemblyNameDefinition ("Unresolvable", new Version (1, 0, 0, 0)), "Unresolvable",
				new ModuleParameters { Kind = ModuleKind.Dll, AssemblyResolver = b_resolver });
			var module = a.MainModule;
			var b_enum = module.ImportReference (b.MainModule.GetType ("StressB.Enum"));
			var attribute_type = new TypeDefinition ("Unresolvable", "XAttribute", TypeAttributes.Public, new TypeReference ("System", "Attribute", module, module.TypeSystem.CoreLibrary));
			var constructor = new MethodDefinition (".ctor", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, module.TypeSystem.Void);
			constructor.Body.Instructions.Add (Instruction.Create (OpCodes.Ret));
			attribute_type.Methods.Add (constructor);
			module.Types.Add (attribute_type);
			var holder = new TypeDefinition ("Unresolvable", "Holder", TypeAttributes.Public, module.TypeSystem.Object);
			var written = new CustomAttribute (constructor);
			written.Properties.Add (new CustomAttributeNamedArgument ("P", new CustomAttributeArgument (module.TypeSystem.Int32, 9)));
			written.Fields.Add (new CustomAttributeNamedArgument ("F", new CustomAttributeArgument (b_enum, (short) 6)));
			holder.CustomAttributes.Add (written);
			module.Types.Add (holder);
			var a_bytes = WriteToBytes (a);

			var resolvable = false;
			var resolver = new HookResolver ();
			var parameters = new ReaderParameters (ReadingMode.Deferred) { AssemblyResolver = resolver };
			resolver.Add (AssemblyDefinition.ReadAssembly (new MemoryStream (b_bytes), parameters));
			var read = AssemblyDefinition.ReadAssembly (new MemoryStream (a_bytes), parameters);
			resolver.Filter = name => resolvable || name.Name != "StressB";

			var attribute = read.MainModule.GetType ("Unresolvable.Holder").CustomAttributes [0];
			var fields = attribute.Fields;
			var properties = attribute.Properties;
			Assert.IsFalse (attribute.IsResolved);
			Assert.AreEqual (0, fields.Count);
			Assert.AreEqual (0, properties.Count);

			resolvable = true;
			Assert.AreEqual (1, attribute.Fields.Count);
			Assert.IsTrue (attribute.IsResolved);
			Assert.AreSame (fields, attribute.Fields);
			Assert.AreSame (properties, attribute.Properties);
			Assert.AreEqual (1, fields.Count);
			Assert.AreEqual (1, properties.Count);
		}

		// A decode that throws publishes nothing. When the decoder filled the attribute in place, the named arguments decoded before an
		// AssemblyResolutionException stayed in Fields, and the next successful decode added them again.
		[Test]
		public void NamedArgumentsAreNotDuplicatedAfterAnAssemblyResolutionException ()
		{
			var b_bytes = BuildDecodeEnumAssembly ();
			var a_bytes = BuildDecodeUser (b_bytes, enum_constructor_argument: false);

			AssemblyDefinition b = null;
			var resolvable = false;
			var resolver = new DefaultAssemblyResolver ();
			resolver.ResolveFailure += (sender, name) => resolvable && name.Name == "DecodeB" ? b : null;
			var parameters = new ReaderParameters (ReadingMode.Deferred) { AssemblyResolver = resolver };
			b = AssemblyDefinition.ReadAssembly (new MemoryStream (b_bytes), parameters);
			var a = AssemblyDefinition.ReadAssembly (new MemoryStream (a_bytes), parameters);
			var attribute = a.MainModule.GetType ("DecodeA.Holder").CustomAttributes [0];

			Assert.Throws<AssemblyResolutionException> (() => { _ = attribute.Fields; });
			Assert.IsFalse (attribute.IsResolved);

			resolvable = true;
			Assert.AreEqual (new [] { "F1", "F2" }, attribute.Fields.Select (f => f.Name).ToArray ());
			Assert.AreEqual (1, attribute.ConstructorArguments.Count);
			Assert.AreEqual (1, attribute.Properties.Count);
			Assert.IsTrue (attribute.IsResolved);
		}

		// Failed decodes keep the ConstructorArguments collection a getter already handed out, and clear it, instead of
		// replacing it with a new one each time.
		[Test]
		public void ConstructorArgumentsHandedOutWhileUnresolvableStayTheSameCollection ()
		{
			var b_bytes = BuildDecodeEnumAssembly ();
			var a_bytes = BuildDecodeUser (b_bytes, enum_constructor_argument: true);

			var resolver = new HookResolver ();
			var parameters = new ReaderParameters (ReadingMode.Deferred) { AssemblyResolver = resolver };
			resolver.Add (AssemblyDefinition.ReadAssembly (new MemoryStream (b_bytes), parameters));
			var a = AssemblyDefinition.ReadAssembly (new MemoryStream (a_bytes), parameters);
			resolver.Filter = name => name.Name != "DecodeB";
			var attribute = a.MainModule.GetType ("DecodeA.Holder").CustomAttributes [0];

			var first = attribute.ConstructorArguments;
			Assert.AreEqual (0, first.Count);
			Assert.AreSame (first, attribute.ConstructorArguments);
			Assert.IsFalse (attribute.IsResolved);

			resolver.Filter = null;
			Assert.AreEqual (1, attribute.ConstructorArguments.Count);
			Assert.IsTrue (attribute.IsResolved);
		}

		static byte [] BuildDecodeEnumAssembly ()
		{
			var assembly = AssemblyDefinition.CreateAssembly (new AssemblyNameDefinition ("DecodeB", new Version (1, 0, 0, 0)), "DecodeB", ModuleKind.Dll);
			var module = assembly.MainModule;
			module.Types.Add (CreateEnum (module, "DecodeB", "Enum", module.TypeSystem.Int16));
			return WriteToBytes (assembly);
		}

		// [X (argument), F1 = 1, F2 = (DecodeB.Enum) 2, P = 3] on DecodeA.Holder, where the argument is an int or a DecodeB.Enum.
		// Fields come before properties in the blob, so F1 is decoded before F2 needs DecodeB.
		static byte [] BuildDecodeUser (byte [] b_bytes, bool enum_constructor_argument)
		{
			var b_resolver = new HookResolver ();
			var b = AssemblyDefinition.ReadAssembly (new MemoryStream (b_bytes), new ReaderParameters { AssemblyResolver = b_resolver });
			b_resolver.Add (b);

			var assembly = AssemblyDefinition.CreateAssembly (new AssemblyNameDefinition ("DecodeA", new Version (1, 0, 0, 0)), "DecodeA",
				new ModuleParameters { Kind = ModuleKind.Dll, AssemblyResolver = b_resolver });
			var module = assembly.MainModule;
			var b_enum = module.ImportReference (b.MainModule.GetType ("DecodeB.Enum"));
			var argument_type = enum_constructor_argument ? b_enum : module.TypeSystem.Int32;

			var attribute_type = new TypeDefinition ("DecodeA", "XAttribute", TypeAttributes.Public, new TypeReference ("System", "Attribute", module, module.TypeSystem.CoreLibrary));
			var constructor = new MethodDefinition (".ctor", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, module.TypeSystem.Void);
			constructor.Parameters.Add (new ParameterDefinition ("value", ParameterAttributes.None, argument_type));
			constructor.Body.Instructions.Add (Instruction.Create (OpCodes.Ret));
			attribute_type.Methods.Add (constructor);
			module.Types.Add (attribute_type);

			var holder = new TypeDefinition ("DecodeA", "Holder", TypeAttributes.Public, module.TypeSystem.Object);
			var attribute = new CustomAttribute (constructor);
			attribute.ConstructorArguments.Add (enum_constructor_argument
				? new CustomAttributeArgument (b_enum, (short) 5)
				: new CustomAttributeArgument (module.TypeSystem.Int32, 5));
			attribute.Fields.Add (new CustomAttributeNamedArgument ("F1", new CustomAttributeArgument (module.TypeSystem.Int32, 1)));
			attribute.Fields.Add (new CustomAttributeNamedArgument ("F2", new CustomAttributeArgument (b_enum, (short) 2)));
			attribute.Properties.Add (new CustomAttributeNamedArgument ("P", new CustomAttributeArgument (module.TypeSystem.Int32, 3)));
			holder.CustomAttributes.Add (attribute);
			module.Types.Add (holder);

			return WriteToBytes (assembly);
		}

		// ---------------------------------------------------------------------------------------------------- helpers

		sealed class HookResolver : IAssemblyResolver {

			readonly Dictionary<string, AssemblyDefinition> assemblies = new Dictionary<string, AssemblyDefinition> ();
			public Action<AssemblyNameReference> Hook;
			public Func<AssemblyNameReference, bool> Filter;

			public void Add (AssemblyDefinition assembly)
			{
				lock (assemblies)
					assemblies [assembly.Name.Name] = assembly;
			}

			public AssemblyDefinition Resolve (AssemblyNameReference name)
			{
				return Resolve (name, new ReaderParameters ());
			}

			public AssemblyDefinition Resolve (AssemblyNameReference name, ReaderParameters parameters)
			{
				var hook = Hook;
				if (hook != null)
					hook (name);

				var filter = Filter;
				if (filter != null && !filter (name))
					return null;

				AssemblyDefinition assembly;
				lock (assemblies)
					assemblies.TryGetValue (name.Name, out assembly);
				return assembly;
			}

			public void Dispose ()
			{
			}
		}

		static byte [] WriteToBytes (AssemblyDefinition assembly)
		{
			var stream = new MemoryStream ();
			assembly.Write (stream);
			return stream.ToArray ();
		}

		static AssemblyDefinition [] ReadPair (byte [] a_bytes, byte [] b_bytes)
		{
			var resolver = new HookResolver ();
			var parameters = new ReaderParameters (ReadingMode.Deferred) { AssemblyResolver = resolver };
			var b = AssemblyDefinition.ReadAssembly (new MemoryStream (b_bytes), parameters);
			var a = AssemblyDefinition.ReadAssembly (new MemoryStream (a_bytes), parameters);
			resolver.Add (b);
			resolver.Add (a);
			return new [] { a, b };
		}

		static TypeDefinition CreateEnum (ModuleDefinition module, string @namespace, string name, TypeReference underlying)
		{
			var type = new TypeDefinition (@namespace, name, TypeAttributes.Public | TypeAttributes.Sealed, new TypeReference ("System", "Enum", module, module.TypeSystem.CoreLibrary));
			type.Fields.Add (new FieldDefinition ("value__", FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName, underlying));
			return type;
		}

		static byte [] BuildStressB ()
		{
			var assembly = AssemblyDefinition.CreateAssembly (new AssemblyNameDefinition ("StressB", new Version (1, 0, 0, 0)), "StressB", ModuleKind.Dll);
			var module = assembly.MainModule;
			module.Types.Add (CreateEnum (module, "StressB", "Enum", module.TypeSystem.Int16));
			module.Types.Add (CreateEnum (module, "StressB", "Flags", module.TypeSystem.Byte));
			module.Types.Add (CreateEnum (module, "StressB", "LongEnum", module.TypeSystem.Int64));
			var outer = new TypeDefinition ("StressB", "Outer", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed, module.TypeSystem.Object);
			module.Types.Add (outer);
			var nested = CreateEnum (module, "", "NestedEnum", module.TypeSystem.UInt32);
			nested.Attributes = TypeAttributes.NestedPublic | TypeAttributes.Sealed;
			outer.NestedTypes.Add (nested);
			module.Types.Add (new TypeDefinition ("StressB", "Blob", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.ExplicitLayout, new TypeReference ("System", "ValueType", module, module.TypeSystem.CoreLibrary)) {
				ClassSize = 16,
				PackingSize = 1,
			});
			return WriteToBytes (assembly);
		}

		static byte [] BuildStressA (byte [] b_bytes, int holders)
		{
			var resolver = new HookResolver ();
			var b = AssemblyDefinition.ReadAssembly (new MemoryStream (b_bytes), new ReaderParameters { AssemblyResolver = resolver });
			resolver.Add (b);
			var assembly = AssemblyDefinition.CreateAssembly (new AssemblyNameDefinition ("StressA", new Version (1, 0, 0, 0)), "StressA",
				new ModuleParameters { Kind = ModuleKind.Dll, AssemblyResolver = resolver });
			var module = assembly.MainModule;
			var ts = module.TypeSystem;
			var b_module = b.MainModule;
			var b_enum = module.ImportReference (b_module.GetType ("StressB.Enum"));
			var b_flags = module.ImportReference (b_module.GetType ("StressB.Flags"));
			var b_long = module.ImportReference (b_module.GetType ("StressB.LongEnum"));
			var b_nested = module.ImportReference (b_module.GetType ("StressB.Outer/NestedEnum"));
			var b_blob = module.ImportReference (b_module.GetType ("StressB.Blob"));
			var system_type = new TypeReference ("System", "Type", module, ts.CoreLibrary);

			// Added after the holders, so the holders' decodes read their members lazily
			var local_enum = CreateEnum (module, "StressA", "LocalEnum", ts.Int32);
			var attribute = new TypeDefinition ("StressA", "SinkAttribute", TypeAttributes.Public, new TypeReference ("System", "Attribute", module, ts.CoreLibrary));
			var constructor = new MethodDefinition (".ctor", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName, ts.Void);
			foreach (var parameter_type in new [] { b_enum, local_enum, ts.Object, new ArrayType (b_enum), system_type, b_nested, b_long, ts.String })
				constructor.Parameters.Add (new ParameterDefinition (parameter_type));
			constructor.Body.Instructions.Add (Instruction.Create (OpCodes.Ret));
			attribute.Methods.Add (constructor);

			Func<int, CustomAttribute> create_attribute = k => {
				var ca = new CustomAttribute (constructor);
				ca.ConstructorArguments.Add (new CustomAttributeArgument (b_enum, (short) (k % 100)));
				ca.ConstructorArguments.Add (new CustomAttributeArgument (local_enum, k));
				ca.ConstructorArguments.Add (new CustomAttributeArgument (ts.Object, new CustomAttributeArgument (b_flags, (byte) (k % 7))));
				ca.ConstructorArguments.Add (new CustomAttributeArgument (new ArrayType (b_enum), new [] { new CustomAttributeArgument (b_enum, (short) 1), new CustomAttributeArgument (b_enum, (short) (k % 5)) }));
				ca.ConstructorArguments.Add (new CustomAttributeArgument (system_type, k % 2 == 0 ? b_nested : local_enum));
				ca.ConstructorArguments.Add (new CustomAttributeArgument (b_nested, (uint) k));
				ca.ConstructorArguments.Add (new CustomAttributeArgument (b_long, (long) k * 1000000000L));
				ca.ConstructorArguments.Add (new CustomAttributeArgument (ts.String, "s" + k));
				ca.Fields.Add (new CustomAttributeNamedArgument ("F", new CustomAttributeArgument (b_flags, (byte) (k % 3))));
				ca.Properties.Add (new CustomAttributeNamedArgument ("P", new CustomAttributeArgument (ts.Object, new CustomAttributeArgument (new ArrayType (b_enum), new [] { new CustomAttributeArgument (b_enum, (short) k) }))));
				ca.Properties.Add (new CustomAttributeNamedArgument ("T", new CustomAttributeArgument (system_type, b_enum)));
				ca.Properties.Add (new CustomAttributeNamedArgument ("N", new CustomAttributeArgument (b_nested, (uint) (k + 1))));
				return ca;
			};

			for (int i = 0; i < holders; i++) {
				var holder = new TypeDefinition ("StressA", "Holder" + i, TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.HasSecurity, ts.Object);
				module.Types.Add (holder);
				holder.CustomAttributes.Add (create_attribute (i * 4));
				holder.CustomAttributes.Add (create_attribute (i * 4 + 1));

				var method = new MethodDefinition ("M", MethodAttributes.Public | MethodAttributes.Static, ts.Int32);
				method.Body.Variables.Add (new VariableDefinition (b_enum));
				var il = method.Body.GetILProcessor ();
				il.Emit (OpCodes.Ldtoken, b_nested);
				il.Emit (OpCodes.Pop);
				il.Emit (OpCodes.Ldc_I4, i);
				il.Emit (OpCodes.Stloc_0);
				il.Emit (OpCodes.Ldloc_0);
				il.Emit (OpCodes.Ret);
				method.CustomAttributes.Add (create_attribute (i * 4 + 2));
				holder.Methods.Add (method);

				var data = new byte [16];
				for (int j = 0; j < data.Length; j++)
					data [j] = (byte) (i + j);
				var field = new FieldDefinition ("Data", FieldAttributes.Assembly | FieldAttributes.Static | FieldAttributes.InitOnly, b_blob) { InitialValue = data };
				field.CustomAttributes.Add (create_attribute (i * 4 + 3));
				holder.Fields.Add (field);

				var declaration = new SecurityDeclaration (SecurityAction.Demand);
				var security_attribute = new SecurityAttribute (attribute);
				security_attribute.Properties.Add (new CustomAttributeNamedArgument ("Value", new CustomAttributeArgument (b_enum, (short) i)));
				security_attribute.Fields.Add (new CustomAttributeNamedArgument ("Array", new CustomAttributeArgument (new ArrayType (b_flags), new [] { new CustomAttributeArgument (b_flags, (byte) (i % 9)) })));
				security_attribute.Properties.Add (new CustomAttributeNamedArgument ("Boxed", new CustomAttributeArgument (ts.Object, new CustomAttributeArgument (b_nested, (uint) i))));
				declaration.SecurityAttributes.Add (security_attribute);
				holder.SecurityDeclarations.Add (declaration);
			}

			module.Types.Add (local_enum);
			module.Types.Add (attribute);
			return WriteToBytes (assembly);
		}

		static string Render (CustomAttributeArgument argument)
		{
			return argument.Type.FullName + "=" + RenderValue (argument.Value);
		}

		static string RenderValue (object value)
		{
			if (value == null)
				return "null";
			if (value is CustomAttributeArgument)
				return "{" + Render ((CustomAttributeArgument) value) + "}";
			var array = value as CustomAttributeArgument [];
			if (array != null)
				return "[" + string.Join (",", array.Select (Render)) + "]";
			var type = value as TypeReference;
			if (type != null)
				return "T:" + type.FullName + "@" + (type.Scope != null ? type.Scope.Name : "?");
			return value.GetType ().Name + ":" + value;
		}

		static string Render (Collection<CustomAttributeNamedArgument> arguments)
		{
			return string.Join (";", arguments.Select (a => a.Name + ":" + Render (a.Argument)));
		}

		static string Render (CustomAttribute attribute)
		{
			return attribute.Constructor.FullName
				+ "(" + string.Join (";", attribute.ConstructorArguments.Select (Render)) + ")"
				+ " F{" + Render (attribute.Fields) + "} P{" + Render (attribute.Properties) + "} "
				+ attribute.HasConstructorArguments + attribute.HasFields + attribute.HasProperties + attribute.IsResolved;
		}

		static List<T> Shuffle<T> (IEnumerable<T> items, Random random)
		{
			var list = items.ToList ();
			return random == null ? list : list.OrderBy (_ => random.Next ()).ToList ();
		}

		static Dictionary<string, string> RenderAttributes (ModuleDefinition module, Random random)
		{
			var providers = new List<KeyValuePair<string, ICustomAttributeProvider>> ();
			foreach (var type in module.Types) {
				providers.Add (new KeyValuePair<string, ICustomAttributeProvider> ("T:" + type.FullName, type));
				foreach (var method in type.Methods)
					providers.Add (new KeyValuePair<string, ICustomAttributeProvider> ("M:" + method.FullName, method));
				foreach (var field in type.Fields)
					providers.Add (new KeyValuePair<string, ICustomAttributeProvider> ("F:" + field.FullName, field));
			}

			var result = new Dictionary<string, string> ();
			foreach (var provider in Shuffle (providers, random))
				result [provider.Key] = string.Join (" || ", provider.Value.CustomAttributes.Select (Render));
			return result;
		}

		static Dictionary<string, string> RenderSecurity (ModuleDefinition module, Random random)
		{
			var result = new Dictionary<string, string> ();
			foreach (var type in Shuffle (module.Types, random)) {
				var builder = new StringBuilder ();
				foreach (var declaration in type.SecurityDeclarations)
					foreach (var attribute in declaration.SecurityAttributes)
						builder.Append (declaration.Action).Append (':').Append (attribute.AttributeType.FullName)
							.Append (" F{").Append (Render (attribute.Fields)).Append ("} P{").Append (Render (attribute.Properties)).Append ("}|");
				result [type.FullName] = builder.ToString ();
			}
			return result;
		}

		static Dictionary<string, string> RenderLazy (ModuleDefinition module, Random random)
		{
			var result = new Dictionary<string, string> ();
			foreach (var type in Shuffle (module.Types, random)) {
				var builder = new StringBuilder ();
				foreach (var field in type.Fields)
					builder.Append (field.Name).Append (" iv=").Append (BitConverter.ToString (field.InitialValue)).Append (';');
				foreach (var method in type.Methods)
					if (method.HasBody)
						builder.Append (method.Name).Append ('{').Append (string.Join (",", method.Body.Instructions.Select (i => i.OpCode.Name + " " + i.Operand)))
							.Append ("} locals=").Append (string.Join (",", method.Body.Variables.Select (v => v.VariableType.FullName))).Append (';');
				result [type.FullName] = builder.ToString ();
			}
			return result;
		}

		static void Compare (List<string> errors, string what, Dictionary<string, string> expected, Dictionary<string, string> actual)
		{
			foreach (var pair in expected) {
				string value;
				if (!actual.TryGetValue (pair.Key, out value) || value != pair.Value)
					lock (errors)
						errors.Add (string.Format ("{0} {1}: expected {2} but was {3}", what, pair.Key, pair.Value, value));
			}
		}
	}
}
