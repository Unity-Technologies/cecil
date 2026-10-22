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
using System.Diagnostics;
using System.Threading;
using Mono.Collections.Generic;

namespace Mono.Cecil {

	public struct CustomAttributeArgument {

		readonly TypeReference type;
		readonly object value;

		public TypeReference Type {
			get { return type; }
		}

		public object Value {
			get { return value; }
		}

		public CustomAttributeArgument (TypeReference type, object value)
		{
			Mixin.CheckType (type);
			this.type = type;
			this.value = value;
		}
	}

	public struct CustomAttributeNamedArgument {

		readonly string name;
		readonly CustomAttributeArgument argument;

		public string Name {
			get { return name; }
		}

		public CustomAttributeArgument Argument {
			get { return argument; }
		}

		public CustomAttributeNamedArgument (string name, CustomAttributeArgument argument)
		{
			Mixin.CheckName (name);
			this.name = name;
			this.argument = argument;
		}
	}

	public interface ICustomAttribute {

		TypeReference AttributeType { get; }

		bool HasFields { get; }
		bool HasProperties { get; }
		bool HasConstructorArguments { get; }
		Collection<CustomAttributeNamedArgument> Fields { get; }
		Collection<CustomAttributeNamedArgument> Properties { get; }
		Collection<CustomAttributeArgument> ConstructorArguments { get; }
	}

	[DebuggerDisplay ("{AttributeType}")]
	public sealed class CustomAttribute : ICustomAttribute {

		internal CustomAttributeValueProjection projection;
		readonly internal uint signature;
		internal volatile bool resolved;
		MethodReference constructor;
		byte [] blob;
		internal Collection<CustomAttributeArgument> arguments;
		internal Collection<CustomAttributeNamedArgument> fields;
		internal Collection<CustomAttributeNamedArgument> properties;

		public MethodReference Constructor {
			get { return constructor; }
			set { constructor = value; }
		}

		public TypeReference AttributeType {
			get { return constructor.DeclaringType; }
		}

		public bool IsResolved {
			get { return resolved; }
		}

		public bool HasConstructorArguments {
			get {
				Resolve ();

				return !arguments.IsNullOrEmpty ();
			}
		}

		public Collection<CustomAttributeArgument> ConstructorArguments {
			get {
				Resolve ();

				if (arguments == null)
					Interlocked.CompareExchange (ref arguments, new Collection<CustomAttributeArgument> (), null);

				return arguments;
			}
		}

		public bool HasFields {
			get {
				Resolve ();

				return !fields.IsNullOrEmpty ();
			}
		}

		public Collection<CustomAttributeNamedArgument> Fields {
			get {
				Resolve ();

				if (fields == null)
					Interlocked.CompareExchange (ref fields, new Collection<CustomAttributeNamedArgument> (), null);

				return fields;
			}
		}

		public bool HasProperties {
			get {
				Resolve ();

				return !properties.IsNullOrEmpty ();
			}
		}

		public Collection<CustomAttributeNamedArgument> Properties {
			get {
				Resolve ();

				if (properties == null)
					Interlocked.CompareExchange (ref properties, new Collection<CustomAttributeNamedArgument> (), null);

				return properties;
			}
		}

		internal bool HasImage {
			get { return constructor != null && constructor.HasImage; }
		}

		internal ModuleDefinition Module {
			get { return constructor.Module; }
		}

		internal CustomAttribute (uint signature, MethodReference constructor)
		{
			this.signature = signature;
			this.constructor = constructor;
			this.resolved = false;
		}

		public CustomAttribute (MethodReference constructor)
		{
			this.constructor = constructor;
			this.resolved = true;
		}

		public CustomAttribute (MethodReference constructor, byte [] blob)
		{
			this.constructor = constructor;
			this.resolved = false;
			this.blob = blob;
		}

		public byte [] GetBlob ()
		{
			if (blob != null)
				return blob;

			if (!HasImage)
				throw new NotSupportedException ();

			return Module.Read (ref blob, this, (attribute, reader) => reader.ReadCustomAttributeBlob (attribute.signature));
		}

		void Resolve ()
		{
			if (resolved || !HasImage)
				return;

			// Decode into a copy without holding the module lock, then publish it under the lock. Decoding resolves the types of
			// enum arguments, which can take the lock of the module defining them, and two modules doing that to each other deadlock.
			// The decoder must only use the signature blob and members that lock for themselves, not the module reader's state.
			var module = Module;
			var decoded = new CustomAttribute (signature, constructor);
			var decoding_failed = false;
			try {
				module.reader.ReadCustomAttributeSignature (decoded);
			} catch (ResolutionException) {
				decoding_failed = true;
			}

			lock (module.SyncRoot) {
				if (resolved)
					return;

				if (decoding_failed) {
					if (arguments != null)
						arguments.Clear ();
					if (fields != null)
						fields.Clear ();
					if (properties != null)
						properties.Clear ();

					return;
				}

				// Collections already handed out receive the decoded values, as when the decoder filled them in place.
				// resolved is written last, and it is volatile, so a reader that sees it set sees the collections
				if (decoded.arguments != null)
					arguments = decoded.arguments;
				Publish (ref fields, decoded.fields);
				Publish (ref properties, decoded.properties);
				resolved = true;
			}
		}

		// The getters install an empty collection with CompareExchange, without the lock, and may already have handed it out.
		// Fill that one, as the decoder used to, and never write over it, which would also write back a null that was read before
		static void Publish (ref Collection<CustomAttributeNamedArgument> collection, Collection<CustomAttributeNamedArgument> decoded)
		{
			if (decoded == null)
				return;

			var current = Interlocked.CompareExchange (ref collection, decoded, null);
			if (current == null)
				return;

			for (int i = 0; i < decoded.Count; i++)
				current.Add (decoded [i]);
		}
	}
}
