using Rhino.PlugIns;
using System;
using System.Runtime.InteropServices;

[assembly: Guid("F5D257EE-2BB1-4B22-BD25-9938E5F0719F")]
// Rhino plug-in developer declarations
[assembly: PlugInDescription(DescriptionType.Organization, "noahk (forked from Critical Software Lab)")]
[assembly: PlugInDescription(DescriptionType.WebSite, "https://github.com/KnuckKnuck0123/obliq")]
[assembly: PlugInDescription(DescriptionType.UpdateUrl, "https://github.com/KnuckKnuck0123/obliq")]
[assembly: PlugInDescription(DescriptionType.Email, "")]
[assembly: PlugInDescription(DescriptionType.Address, "Original Authors: Galo Canizares, Critical Software Lab")]

namespace Obliq
{
    public class ObliqPlugin : PlugIn
    {
        public ObliqPlugin()
        {
            Instance = this;
        }

        public static ObliqPlugin Instance { get; private set; }

        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            return LoadReturnCode.Success;
        }
    }
}
