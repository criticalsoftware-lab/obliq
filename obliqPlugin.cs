using obliq.Core;

namespace obliq
{
    ///<summary>
    /// <para>Every RhinoCommon .rhp assembly must have one and only one PlugIn-derived
    /// class. DO NOT create instances of this class yourself. It is the
    /// responsibility of Rhino to create an instance of this class.</para>
    /// <para>Plug-in description attributes live in Properties/AssemblyInfo.cs.</para>
    ///</summary>
    public class obliqPlugin : Rhino.PlugIns.PlugIn
    {
        public obliqPlugin()
        {
            Instance = this;
        }

        ///<summary>Gets the only instance of the obliqPlugin plug-in.</summary>
        public static obliqPlugin Instance { get; private set; }

        protected override void OnShutdown()
        {
            // Stop the shear conduit when Rhino closes.
            ObliqueConduit.Instance.Enabled = false;
            base.OnShutdown();
        }
    }
}
