using System;
using System.Threading;

using Java.Interop;

using NUnit.Framework;

namespace Java.InteropTests
{
	[TestFixture]
	public class JavaObjectTest
	{
		[Test]
		public void JavaReferencedInstanceSurvivesCollection ()
		{
			Console.WriteLine ("JavaReferencedInstanceSurvivesCollection");
			using (var t = new JniType ("java/lang/Object")) {
				var lrefArray = JniEnvironment.Arrays.NewObjectArray (1, t.SafeHandle, JniReferenceSafeHandle.Null);
				var grefArray = lrefArray.NewGlobalRef ();
				lrefArray.Dispose ();
				var oldHandle = IntPtr.Zero;
				var w = new Thread (() => {
						var v       = new JavaObject ();
						oldHandle   = v.SafeHandle.DangerousGetHandle ();
						v.RegisterWithVM ();
						JniEnvironment.Arrays.SetObjectArrayElement (grefArray, 0, v.SafeHandle);
				});
				w.Start ();
				w.Join ();
				GC.Collect ();
				GC.WaitForPendingFinalizers ();
				var first = JniEnvironment.Arrays.GetObjectArrayElement (grefArray, 0);
				Assert.IsNotNull (JVM.Current.PeekObject (first));
				var o = (JavaObject) JVM.Current.GetObject (first, JniHandleOwnership.Transfer);
				if (oldHandle != o.SafeHandle.DangerousGetHandle ()) {
					Console.WriteLine ("Yay, object handle changed; value survived a GC!");
				} else {
					Console.WriteLine ("What is this, Android pre-ICS?!");
				}
				o.Dispose ();
				grefArray.Dispose ();
			}
		}

		[Test]
		public void RegisterWithVM ()
		{
			int registeredCount = JVM.Current.GetSurfacedObjects ().Count;
			JniLocalReference l;
			JavaObject o;
			using (o = new JavaObject ()) {
				l   = o.SafeHandle.NewLocalRef ();
				Assert.AreEqual (JniReferenceType.Local, o.SafeHandle.ReferenceType);
				Assert.AreEqual (registeredCount, JVM.Current.GetSurfacedObjects ().Count);
				Assert.IsNull (JVM.Current.PeekObject (l));
				o.RegisterWithVM ();
				Assert.AreNotSame (l, o.SafeHandle);
				Assert.AreEqual (JniReferenceType.Global, o.SafeHandle.ReferenceType);
				l.Dispose ();
				l = o.SafeHandle.NewLocalRef ();
				Assert.AreEqual (registeredCount + 1, JVM.Current.GetSurfacedObjects ().Count);
				Assert.AreSame (o, JVM.Current.PeekObject (l));
			}
			Assert.AreEqual (registeredCount, JVM.Current.GetSurfacedObjects ().Count);
			Assert.IsNull (JVM.Current.PeekObject (l));
			l.Dispose ();
			Assert.Throws<ObjectDisposedException> (() => o.RegisterWithVM ());
		}

		[Test]
		public void RegisterWithVM_ThrowsOnDuplicateEntry ()
		{
			using (var original = new JavaObject ()) {
				original.RegisterWithVM ();
				using (var alias    = new JavaObject (original.SafeHandle, JniHandleOwnership.DoNotTransfer)) {
					Assert.Throws<NotSupportedException> (() => alias.RegisterWithVM ());
				}
			}
		}

		[Test]
		public void UnreferencedInstanceIsCollected ()
		{
			JniLocalReference oldHandle = null;
			WeakReference r = null;
			var t = new Thread (() => {
					var v     = new JavaObject ();
					oldHandle = v.SafeHandle.NewLocalRef ();
					r         = new WeakReference (v);
					v.RegisterWithVM ();
			});
			t.Start ();
			t.Join ();
			GC.Collect ();
			GC.WaitForPendingFinalizers ();
			Assert.IsFalse (r.IsAlive);
			Assert.IsNull (r.Target);
			Assert.IsNull (JVM.Current.PeekObject (oldHandle));
			oldHandle.Dispose ();
		}

		[Test]
		public void Dispose ()
		{
			var d = false;
			var f = false;
			var o = new JavaDisposedObject (() => d = true, () => f = true);
			o.Dispose ();
			Assert.IsTrue (d);
			Assert.IsFalse (f);
		}

		[Test]
		public void Dispose_Finalized ()
		{
			var d = false;
			var f = false;
			var t = new Thread (() => {
				var v     = new JavaDisposedObject (() => d = true, () => f = true);
				GC.KeepAlive (v);
			});
			t.Start ();
			t.Join ();
			GC.Collect ();
			GC.WaitForPendingFinalizers ();
			Assert.IsFalse (d);
			Assert.IsTrue (f);
		}

		[Test]
		public void ObjectDisposed ()
		{
			var o = new JavaObject ();
			o.Dispose ();

			// These should not throw
			var h = o.SafeHandle;
			var p = o.JniPeerMembers;

			// These should throw
			Assert.Throws<ObjectDisposedException> (() => o.GetHashCode ());
			Assert.Throws<ObjectDisposedException> (() => o.RegisterWithVM ());
			Assert.Throws<ObjectDisposedException> (() => o.ToString ());
			Assert.Throws<ObjectDisposedException> (() => o.Equals (o));
		}

		[Test]
		public void Ctor ()
		{
			using (var t = new JniType ("java/lang/Object"))
			using (var c = t.GetConstructor ("()V")) {
				var lref = t.NewObject (c);
				using (var o = new JavaObject (lref, JniHandleOwnership.DoNotTransfer)) {
					Assert.IsFalse (lref.IsInvalid);
					Assert.AreNotSame (lref, o.SafeHandle);
				}
				using (var o = new JavaObject (lref, JniHandleOwnership.Transfer)) {
					Assert.IsTrue (lref.IsInvalid);
					Assert.AreNotSame (lref, o.SafeHandle);
				}
			}
		}

		[Test]
		public void Ctor_Exceptions ()
		{
			Assert.Throws<ArgumentNullException> (() => new JavaObject (null, JniHandleOwnership.Transfer));
			Assert.Throws<ArgumentException> (() => new JavaObject (new JniInvocationHandle (IntPtr.Zero), JniHandleOwnership.Transfer));

			// Note: This may break if/when JavaVM provides "default"
			Assert.Throws<NotSupportedException> (() => new JavaObjectWithNoJavaPeer ());
			Assert.Throws<JavaException> (() => new JavaObjectWithMissingJavaPeer ());
		}
	}

	class JavaObjectWithNoJavaPeer : JavaObject {
	}

	[JniTypeInfo ("__this__/__type__/__had__/__better__/__not__/__Exist__")]
	class JavaObjectWithMissingJavaPeer : JavaObject {
	}

	[JniTypeInfo ("java/lang/Object")]
	class JavaDisposedObject : JavaObject {

		public Action   OnDisposed;
		public Action   OnFinalized;

		public JavaDisposedObject (Action disposed, Action finalized)
		{
			OnDisposed  = disposed;
			OnFinalized = finalized;
		}

		protected override void Dispose (bool disposing)
		{
			if (disposing)
				OnDisposed ();
			else
				OnFinalized ();
		}
	}
}

