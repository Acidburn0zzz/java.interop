﻿using System;
using System.Collections.Generic;

namespace Java.Interop
{
	public sealed partial class JniPeerInstanceMethods
	{
		internal JniPeerInstanceMethods (JniPeerMembers members)
		{
			DeclaringType   = members.ManagedPeerType;
			JniPeerType     = members.JniPeerType;
		}

		JniPeerInstanceMethods (Type declaringType)
		{
			var jvm     = JniEnvironment.Current.JavaVM;
			var info    = jvm.GetJniTypeInfoForType (declaringType);
			if (info.JniTypeName == null)
				throw new NotSupportedException (
						string.Format ("Cannot create instance of type '{0}': no Java peer type found.",
							declaringType.FullName));

			DeclaringType   = declaringType;
			JniPeerType     = new JniType (info.ToString ());
			JniPeerType.RegisterWithVM ();
		}

		readonly Type                                       DeclaringType;

		JniType                                             JniPeerType;
		Dictionary<string, JniInstanceMethodID>             InstanceMethods = new Dictionary<string, JniInstanceMethodID>();
		Dictionary<Type, JniPeerInstanceMethods>            SubclassConstructors = new Dictionary<Type, JniPeerInstanceMethods> ();

		internal void Dispose ()
		{
			if (JniPeerType == null)
				return;

			// Don't dispose JniPeerType; it's shared with others.
			foreach (var m in InstanceMethods.Values)
				m.Dispose ();
			InstanceMethods         = null;

			foreach (var p in SubclassConstructors.Values)
				p.Dispose ();
			SubclassConstructors    = null;

			JniPeerType = null;
		}

		public JniInstanceMethodID GetConstructor (string signature)
		{
			if (signature == null)
				throw new ArgumentNullException ("signature");
			lock (InstanceMethods) {
				JniInstanceMethodID m;
				if (!InstanceMethods.TryGetValue (signature, out m)) {
					m = JniPeerType.GetConstructor (signature);
					InstanceMethods.Add (signature, m);
				}
				return m;
			}
		}

		JniPeerInstanceMethods GetConstructorsForType (Type declaringType)
		{
			if (declaringType == DeclaringType)
				return this;

			JniPeerInstanceMethods methods;
			lock (SubclassConstructors) {
				if (!SubclassConstructors.TryGetValue (declaringType, out methods)) {
					methods = new JniPeerInstanceMethods (declaringType);
					SubclassConstructors.Add (declaringType, methods);
				}
			}
			return methods;
		}

		public JniInstanceMethodID GetMethodID (string encodedMember)
		{
			lock (InstanceMethods) {
				JniInstanceMethodID m;
				if (!InstanceMethods.TryGetValue (encodedMember, out m)) {
					string method, signature;
					JniPeerMembers.GetNameAndSignature (encodedMember, out method, out signature);
					m = JniPeerType.GetInstanceMethod (method, signature);
					InstanceMethods.Add (encodedMember, m);
				}
				return m;
			}
		}

		public unsafe JniLocalReference StartCreateInstance (string constructorSignature, Type declaringType, JValue* parameters)
		{
			if (JniEnvironment.Current.JavaVM.NewObjectRequired) {
				return NewObject (constructorSignature, declaringType, parameters);
			}
			using (var lref = GetConstructorsForType (declaringType)
					.JniPeerType
					.AllocObject ())
				return lref.ToAllocObjectRef ();
		}

		unsafe JniLocalReference NewObject (string constructorSignature, Type declaringType, JValue* parameters)
		{
			var methods = GetConstructorsForType (declaringType);
			var ctor    = methods.GetConstructor (constructorSignature);
			return methods.JniPeerType.NewObject (ctor, parameters);
		}

		public unsafe void FinishCreateInstance (string constructorSignature, IJavaObject self, JValue* parameters)
		{
			if (JniEnvironment.Current.JavaVM.NewObjectRequired) {
				return;
			}
			var methods = GetConstructorsForType (self.GetType ());
			var ctor    = methods.GetConstructor (constructorSignature);
			ctor.CallNonvirtualVoidMethod (self.SafeHandle, methods.JniPeerType.SafeHandle, parameters);
		}
	}

	struct JniArgumentMarshalInfo<T> {
		JValue                          jvalue;
		JniLocalReference               lref;
		IJavaObject                     obj;
		Action<IJavaObject, object>     cleanup;

		internal JniArgumentMarshalInfo (T value)
		{
			this        = new JniArgumentMarshalInfo<T> ();
			var jvm     = JniEnvironment.Current.JavaVM;
			var info    = jvm.GetJniMarshalInfoForType (typeof (T));
			if (info.CreateJValue != null)
				jvalue = info.CreateJValue (value);
			else if (info.CreateMarshalCollection != null) {
				obj     = info.CreateMarshalCollection (value);
				jvalue  = new JValue (obj);
			} else if (info.CreateLocalRef != null) {
				lref    = info.CreateLocalRef (value);
				jvalue  = new JValue (lref);
			} else
				throw new NotSupportedException ("Don't know how to get a JValue for: " + typeof (T).FullName);
			cleanup     = info.CleanupMarshalCollection;
		}

		public JValue JValue {
			get {return jvalue;}
		}

		public  void    Cleanup (object value)
		{
			if (cleanup != null && obj != null)
				cleanup (obj, value);
			if (lref != null)
				lref.Dispose ();
		}
	}
}

