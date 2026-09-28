using System.Runtime.InteropServices;
using Rhino.PlugIns;

// Plug-in description attributes (ported from ObliqPlugIn.cpp).
[assembly: PlugInDescription(DescriptionType.Organization, "Critical Software Lab")]
[assembly: PlugInDescription(DescriptionType.Address, "University of Kentucky\r\nLexington KY 40506")]
[assembly: PlugInDescription(DescriptionType.Country, "USA")]
[assembly: PlugInDescription(DescriptionType.Email, "galo.canizares@uky.edu")]
[assembly: PlugInDescription(DescriptionType.WebSite, "https://github.com/criticalsoftware-lab")]
[assembly: PlugInDescription(DescriptionType.UpdateUrl, "https://github.com/criticalsoftware-lab/obliq")]

// Unique plug-in ID. Deliberately different from the C++ plug-in
// (F5D257EE-2BB1-4B22-BD25-9938E5F0719F) so Rhino treats them as separate plug-ins.
[assembly: Guid("44687BC9-D1E4-4241-A342-15A972AE0AA1")]
