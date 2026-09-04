using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("YARG-VR")]
[assembly: AssemblyProduct("YARG-VR")]
[assembly: AssemblyVersion("1.3.19.0")]
[assembly: AssemblyFileVersion("1.3.19.0")]

// MelonLoader mod registration.
// YARG v0.15.0 ships YARG_Data/app.info = "YARC" / "YARG" (confirmed from the release package
// and ProjectSettings.asset: companyName=YARC, productName=YARG).
[assembly: MelonLoader.MelonInfo(typeof(YargVr.VrMod), "YARG-VR", "1.3.19", "YARG-VR Project")]
[assembly: MelonLoader.MelonGame("YARC", "YARG")]
