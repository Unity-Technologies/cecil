using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

using Mono.Cecil;
using Mono.Cecil.Cil;

using NUnit.Framework;

namespace Mono.Cecil.Tests {

	[TestFixture]
	public class ResolveTests : BaseTestFixture {

		[Test]
		public void StringEmpty ()
		{
			var string_empty = GetReference<Func<string>, FieldReference> (
				() => string.Empty);

			Assert.AreEqual ("System.String System.String::Empty", string_empty.FullName);

			var definition = string_empty.Resolve ();

			Assert.IsNotNull (definition);

			Assert.AreEqual ("System.String System.String::Empty", definition.FullName);
			Assert.AreEqual (Platform.OnCoreClr ? "System.Private.CoreLib" : "mscorlib", 
				definition.Module.Assembly.Name.Name);
		}

		delegate string GetSubstring (string str, int start, int length);

		[Test]
		public void StringSubstring ()
		{
			var string_substring = GetReference<GetSubstring, MethodReference> (
				(s, start, length) => s.Substring (start, length));

			var definition = string_substring.Resolve ();

			Assert.IsNotNull (definition);

			Assert.AreEqual ("System.String System.String::Substring(System.Int32,System.Int32)", definition.FullName);
			Assert.AreEqual (Platform.OnCoreClr ? "System.Private.CoreLib" : "mscorlib",
				definition.Module.Assembly.Name.Name);
		}

		[Test]
		public void StringLength ()
		{
			var string_length = GetReference<Func<string, int>, MethodReference> (s => s.Length);

			var definition = string_length.Resolve ();

			Assert.IsNotNull (definition);

			Assert.AreEqual ("get_Length", definition.Name);
			Assert.AreEqual ("System.String", definition.DeclaringType.FullName);
			Assert.AreEqual (Platform.OnCoreClr ? "System.Private.CoreLib" : "mscorlib", 
				definition.Module.Assembly.Name.Name);
		}

		[Test]
		public void ListOfStringAdd ()
		{
			var list_add = GetReference<Action<List<string>>, MethodReference> (
				list => list.Add ("coucou"));

			Assert.AreEqual ("System.Void System.Collections.Generic.List`1<System.String>::Add(!0)", list_add.FullName);

			var definition = list_add.Resolve ();

			Assert.IsNotNull (definition);

			Assert.AreEqual ("System.Void System.Collections.Generic.List`1::Add(T)", definition.FullName);
			Assert.AreEqual (Platform.OnCoreClr ? "System.Private.CoreLib" : "mscorlib",
				definition.Module.Assembly.Name.Name);
		}

		[Test]
		public void DictionaryOfStringTypeDefinitionTryGetValue ()
		{
			var try_get_value = GetReference<Func<Dictionary<string, TypeDefinition>, string, bool>, MethodReference> (
				(d, s) => {
					TypeDefinition type;
					return d.TryGetValue (s, out type);
				});

			Assert.AreEqual ("System.Boolean System.Collections.Generic.Dictionary`2<System.String,Mono.Cecil.TypeDefinition>::TryGetValue(!0,!1&)",
				try_get_value.FullName);

			var definition = try_get_value.Resolve ();

			Assert.IsNotNull (definition);

			Assert.AreEqual ("System.Boolean System.Collections.Generic.Dictionary`2::TryGetValue(TKey,TValue&)", definition.FullName);
			Assert.AreEqual (Platform.OnCoreClr ? "System.Private.CoreLib" : "mscorlib",
				 definition.Module.Assembly.Name.Name);
		}

		class CustomResolver : DefaultAssemblyResolver {

			public void Register (AssemblyDefinition assembly)
			{
				this.RegisterAssembly (assembly);
				this.AddSearchDirectory (Path.GetDirectoryName (assembly.MainModule.FileName));
			}
		}

		[Test]
		public void ExportedTypeFromModule ()
		{
			var resolver = new CustomResolver ();
			var parameters = new ReaderParameters { AssemblyResolver = resolver };
			var mma = GetResourceModule ("mma.exe", parameters);

			resolver.Register (mma.Assembly);

			using (var current_module = GetCurrentModule (parameters)) {
				var reference = new TypeReference ("Module.A", "Foo", current_module, AssemblyNameReference.Parse (mma.Assembly.FullName), false);

				var definition = reference.Resolve ();
				Assert.IsNotNull (definition);
				Assert.AreEqual ("Module.A.Foo", definition.FullName);
			}
		}

		[Test]
		public void TypeForwarder ()
		{
			var resolver = new CustomResolver ();
			var parameters = new ReaderParameters { AssemblyResolver = resolver };

			var types = ModuleDefinition.ReadModule (
				CompilationService.CompileResource (GetCSharpResourcePath ("CustomAttributes.cs")),
				parameters);

			resolver.Register (types.Assembly);

			var current_module = GetCurrentModule (parameters);
			var reference = new TypeReference ("System.Diagnostics", "DebuggableAttribute", current_module, AssemblyNameReference.Parse (types.Assembly.FullName), false);

			var definition = reference.Resolve ();
			Assert.IsNotNull (definition);
			Assert.AreEqual ("System.Diagnostics.DebuggableAttribute", definition.FullName);
			Assert.AreEqual (Platform.OnCoreClr ? "System.Private.CoreLib" : "mscorlib", definition.Module.Assembly.Name.Name);
		}

		[Test]
		public void TypeForwarderCircularity ()
		{
			var resolver = new CustomResolver();
			var parameters = new ReaderParameters { AssemblyResolver = resolver };
			
			var module1 = GetResourceModule("TypeForwarderLoop1.dll", parameters);
			var module2 = GetResourceModule("TypeForwarderLoop2.dll", parameters);

			resolver.Register(module1.Assembly);
			resolver.Register(module2.Assembly);

			var reference = new TypeReference("MyNamespace", "MyType", module1, AssemblyNameReference.Parse(module1.Assembly.FullName), false);

			Assert.Throws<InvalidOperationException>(() => { _ = reference.Resolve(); });
		}

		[Test]
		public void TypeForwarderResolvedByTwoThreads ()
		{
			var target = AssemblyDefinition.CreateAssembly (new AssemblyNameDefinition ("ForwardTarget", new Version (1, 0, 0, 0)), "ForwardTarget", ModuleKind.Dll);
			target.MainModule.Types.Add (new TypeDefinition ("ForwardTarget", "Type", TypeAttributes.Public, target.MainModule.TypeSystem.Object));

			var inside = new ManualResetEvent (false);
			var release = new ManualResetEvent (false);
			var resolver = new BlockingResolver (target, inside, release);

			var forwarder = AssemblyDefinition.CreateAssembly (
				new AssemblyNameDefinition ("Forwarder", new Version (1, 0, 0, 0)),
				"Forwarder",
				new ModuleParameters { Kind = ModuleKind.Dll, AssemblyResolver = resolver });
			var target_reference = new AssemblyNameReference ("ForwardTarget", new Version (1, 0, 0, 0));
			forwarder.MainModule.AssemblyReferences.Add (target_reference);
			var exported = new ExportedType ("ForwardTarget", "Type", forwarder.MainModule, target_reference) { IsForwarder = true };
			forwarder.MainModule.ExportedTypes.Add (exported);

			// The first thread stops inside Resolve, while the second one resolves the same forwarder
			TypeDefinition first_result = null;
			Exception first_error = null;
			var first = new Thread (() => {
				try {
					first_result = exported.Resolve ();
				} catch (Exception e) {
					first_error = e;
				}
			}) { IsBackground = true };
			first.Start ();

			try {
				Assert.IsTrue (inside.WaitOne (TimeSpan.FromSeconds (30)));
				resolver.Block = false;

				Assert.AreEqual ("ForwardTarget.Type", exported.Resolve ().FullName);
			} finally {
				release.Set ();
				Assert.IsTrue (first.Join (TimeSpan.FromSeconds (30)));
			}

			Assert.IsNull (first_error);
			Assert.AreEqual ("ForwardTarget.Type", first_result.FullName);
		}

		// The per-thread guard must be empty again when Resolve throws, or the next resolution on that thread would see a circularity
		[Test]
		public void CircularityGuardIsClearedWhenResolveThrows ()
		{
			var target = AssemblyDefinition.CreateAssembly (new AssemblyNameDefinition ("GuardTarget", new Version (1, 0, 0, 0)), "GuardTarget", ModuleKind.Dll);
			target.MainModule.Types.Add (new TypeDefinition ("GuardTarget", "Type", TypeAttributes.Public, target.MainModule.TypeSystem.Object));

			var resolver = new FailingResolver (target);
			var forwarder = AssemblyDefinition.CreateAssembly (
				new AssemblyNameDefinition ("GuardForwarder", new Version (1, 0, 0, 0)),
				"GuardForwarder",
				new ModuleParameters { Kind = ModuleKind.Dll, AssemblyResolver = resolver });
			var target_reference = new AssemblyNameReference ("GuardTarget", new Version (1, 0, 0, 0));
			forwarder.MainModule.AssemblyReferences.Add (target_reference);
			var exported = new ExportedType ("GuardTarget", "Type", forwarder.MainModule, target_reference) { IsForwarder = true };
			forwarder.MainModule.ExportedTypes.Add (exported);

			Assert.Throws<AssemblyResolutionException> (() => exported.Resolve ());
			Assert.AreEqual (0, ExportedTypesBeingResolved ());

			resolver.Fail = false;
			Assert.AreEqual ("GuardTarget.Type", exported.Resolve ().FullName);
			Assert.AreEqual (0, ExportedTypesBeingResolved ());
		}

		[Test]
		public void CircularityGuardIsClearedAfterACircularity ()
		{
			var resolver = new CustomResolver ();
			var parameters = new ReaderParameters { AssemblyResolver = resolver };

			var module1 = GetResourceModule ("TypeForwarderLoop1.dll", parameters);
			var module2 = GetResourceModule ("TypeForwarderLoop2.dll", parameters);

			resolver.Register (module1.Assembly);
			resolver.Register (module2.Assembly);

			var reference = new TypeReference ("MyNamespace", "MyType", module1, AssemblyNameReference.Parse (module1.Assembly.FullName), false);

			for (int i = 0; i < 2; i++) {
				var exception = Assert.Throws<InvalidOperationException> (() => { _ = reference.Resolve (); });
				StringAssert.Contains ("Circularity", exception.Message);
				Assert.AreEqual (0, ExportedTypesBeingResolved ());
			}
		}

		// The guard is a private thread static, which holds the state of the calling thread. Before it existed, the flag was on the exported type.
		static int ExportedTypesBeingResolved ()
		{
			var field = typeof (ExportedType).GetField ("resolving", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
			if (field == null)
				return 0;

			var set = (System.Collections.IEnumerable) field.GetValue (null);
			return set == null ? 0 : set.Cast<object> ().Count ();
		}

		class FailingResolver : IAssemblyResolver {

			readonly AssemblyDefinition assembly;

			public volatile bool Fail = true;

			public FailingResolver (AssemblyDefinition assembly)
			{
				this.assembly = assembly;
			}

			public AssemblyDefinition Resolve (AssemblyNameReference name)
			{
				return Resolve (name, new ReaderParameters ());
			}

			public AssemblyDefinition Resolve (AssemblyNameReference name, ReaderParameters parameters)
			{
				if (Fail)
					throw new AssemblyResolutionException (name);

				return assembly;
			}

			public void Dispose ()
			{
			}
		}

		class BlockingResolver : IAssemblyResolver {

			readonly AssemblyDefinition assembly;
			readonly ManualResetEvent inside;
			readonly ManualResetEvent release;

			public volatile bool Block = true;

			public BlockingResolver (AssemblyDefinition assembly, ManualResetEvent inside, ManualResetEvent release)
			{
				this.assembly = assembly;
				this.inside = inside;
				this.release = release;
			}

			public AssemblyDefinition Resolve (AssemblyNameReference name)
			{
				return Resolve (name, new ReaderParameters ());
			}

			public AssemblyDefinition Resolve (AssemblyNameReference name, ReaderParameters parameters)
			{
				if (Block) {
					inside.Set ();
					release.WaitOne (TimeSpan.FromSeconds (30));
				}

				return assembly;
			}

			public void Dispose ()
			{
			}
		}

		[Test]
		public void NestedTypeForwarder ()
		{
			var resolver = new CustomResolver ();
			var parameters = new ReaderParameters { AssemblyResolver = resolver };

			var types = ModuleDefinition.ReadModule (
				CompilationService.CompileResource (GetCSharpResourcePath ("CustomAttributes.cs")),
				parameters);

			resolver.Register (types.Assembly);

			var current_module = GetCurrentModule (parameters);
			var reference = new TypeReference ("", "DebuggingModes", current_module, null, true);
			reference.DeclaringType = new TypeReference ("System.Diagnostics", "DebuggableAttribute", current_module, AssemblyNameReference.Parse (types.Assembly.FullName), false);

			var definition = reference.Resolve ();
			Assert.IsNotNull (definition);
			Assert.AreEqual ("System.Diagnostics.DebuggableAttribute/DebuggingModes", definition.FullName);
			Assert.AreEqual (Platform.OnCoreClr ? "System.Private.CoreLib" : "mscorlib", definition.Module.Assembly.Name.Name);
		}

		[Test]
		public void RectangularArrayResolveGetMethod ()
		{
			var get_a_b = GetReference<Func<int[,], int>, MethodReference> (matrix => matrix [2, 2]);

			Assert.AreEqual ("Get", get_a_b.Name);
			Assert.IsNotNull (get_a_b.Module);
			Assert.IsNull (get_a_b.Resolve ());
		}

		[Test]
		public void GenericRectangularArrayGetMethodInMemberReferences ()
		{
			using (var module = GetResourceModule ("FSharp.Core.dll")) {
				foreach (var member in module.GetMemberReferences ()) {
					if (!member.DeclaringType.IsArray)
						continue;

					Assert.IsNull (member.Resolve ());
				}
			}
		}

		[Test]
		public void ResolveFunctionPointer ()
		{
			var module = GetResourceModule ("cppcli.dll");
			var global = module.GetType ("<Module>");
			var field = global.GetField ("__onexitbegin_app_domain");

			var type = field.FieldType as PointerType;
			Assert.IsNotNull(type);

			var fnptr = type.ElementType as FunctionPointerType;
			Assert.IsNotNull (fnptr);

			Assert.IsNull (fnptr.Resolve ());
		}

		[Test]
		public void ResolveGenericParameter ()
		{
			var collection = typeof (Mono.Collections.Generic.Collection<>).ToDefinition ();
			var parameter = collection.GenericParameters [0];

			Assert.IsNotNull (parameter);

			Assert.IsNull (parameter.Resolve ());
		}

		[Test]
		public void ResolveNullVersionAssembly ()
		{
			var reference = AssemblyNameReference.Parse ("System.Core");
			reference.Version = null;

			var resolver = new DefaultAssemblyResolver ();
			Assert.IsNotNull (resolver.Resolve (reference));
		}

		[Test]
		public void ResolvePortableClassLibraryReference ()
		{
			var resolver = new DefaultAssemblyResolver ();
			var parameters = new ReaderParameters { AssemblyResolver = resolver };
			var pcl = GetResourceModule ("PortableClassLibrary.dll", parameters);

			foreach (var reference in pcl.AssemblyReferences) {
				Assert.IsTrue (reference.IsRetargetable);
				var assembly = resolver.Resolve (reference);
				Assert.IsNotNull (assembly);

				if (!Platform.OnCoreClr)
					Assert.AreEqual (typeof (object).Assembly.GetName ().Version, assembly.Name.Version);
			}
		}

		[Test]
		public void ResolveModuleReferenceFromMemberReferenceTest ()
		{
			using (var mma = AssemblyDefinition.ReadAssembly (GetAssemblyResourcePath ("mma.exe"))) {
				var modB = mma.Modules [2];
				var bazType = modB.GetType ("Module.B.Baz");
				var gazonkMethod = bazType.Methods.First (m => m.Name.Equals ("Gazonk"));
				var callInstr = gazonkMethod.Body.Instructions [1];

				var methodRef = callInstr.Operand as MethodReference;
				var methodTypeRef = methodRef.DeclaringType;

				Assert.AreEqual (mma, methodTypeRef.Module.Assembly);

				var def = methodTypeRef.Resolve ();
				Assert.IsNotNull (def);
				Assert.AreEqual ("Module.A.Foo", def.FullName);
			}
		}

		[Test]
		public void ResolveModuleReferenceFromMemberReferenceOfSingleNetModuleTest ()
		{
			using (var modb = ModuleDefinition.ReadModule (GetAssemblyResourcePath ("modb.netmodule"))) {
				var bazType = modb.GetType ("Module.B.Baz");
				var gazonkMethod = bazType.Methods.First (m => m.Name.Equals ("Gazonk"));
				var callInstr = gazonkMethod.Body.Instructions [1];

				var methodRef = callInstr.Operand as MethodReference;
				var methodTypeRef = methodRef.DeclaringType;

				Assert.IsNull (methodTypeRef.Module.Assembly);
				Assert.IsNull (methodTypeRef.Resolve ());
			}
		}
		TRet GetReference<TDel, TRet> (TDel code)
		{
			var @delegate = code as Delegate;
			if (@delegate == null)
				throw new InvalidOperationException ();

			var reference = (TRet) GetReturnee (GetMethodFromDelegate (@delegate));

			Assert.IsNotNull (reference);

			return reference;
		}

		static object GetReturnee (MethodDefinition method)
		{
			Assert.IsTrue (method.HasBody);

			var instruction = method.Body.Instructions [method.Body.Instructions.Count - 1];

			Assert.IsNotNull (instruction);

			while (instruction != null) {
				var opcode = instruction.OpCode;
				switch (opcode.OperandType) {
				case OperandType.InlineField:
				case OperandType.InlineTok:
				case OperandType.InlineType:
				case OperandType.InlineMethod:
					return instruction.Operand;
				default:
					instruction = instruction.Previous;
					break;
				}
			}

			throw new InvalidOperationException ();
		}

		MethodDefinition GetMethodFromDelegate (Delegate @delegate)
		{
			var method = @delegate.Method;
			var type = (TypeDefinition) TypeParser.ParseType (GetCurrentModule (), method.DeclaringType.FullName);

			Assert.IsNotNull (type);

			return type.Methods.Where (m => m.Name == method.Name).First ();
		}
	}
}
