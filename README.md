# UAssetManager Model Studio

A UAssetManager plugin for previewing FBX and PMX models, validating bone mappings, rebinding skinned meshes to a target skeleton, and exporting the results as FBX.

## Install

1. In UAssetManager 1.0.2 or later on Windows x64, choose **Plugins → Install from Repository…**.
2. Paste `https://github.com/XyliaUp/UAssetManager_ModelStudio`, select **Find Release**, and install the listed package.
3. Restart UAssetManager and open **Plugins → Model Studio**.

Model Studio uses an installed x64 FBX SDK 2018.1.1 runtime. Select the directory containing `libfbxsdk.dll` under **Environment**, or select an Unreal Engine 4.25 installation that contains it. The app also checks `MODEL_STUDIO_FBX_SDK`.

## Export a model

1. Select a source FBX or PMX and a reference FBX containing a single skinned mesh.
2. Read the target skeleton, then map each source bone with positive skin weights. Click a missing bone to jump to its row. You can import or export mappings as TSV.
3. Select **Export FBX**. Each export creates a new folder with `model.fbx` and any copied diffuse textures. Keep the textures beside the FBX when you move the model.

## Build the Release ZIP

Use an x64 Visual Studio developer terminal with the .NET 10 SDK and Python. Supply the UAssetManager build output containing `UAssetAPI.dll` and `UAssetAPI.Pak.dll`, plus a local UE 4.25 installation with the FBX SDK headers and import library.

```powershell
./Build-Release.ps1 -HostReferenceDirectory <host-build-output> -UnrealRoot <ue-4.25-directory>
```

Attach `dist/UAssetManager_ModelStudio-win-x64.zip` to the GitHub Release. The ZIP includes the plugin, its manifest, and the native bridge. The host and the local FBX SDK supply their runtime libraries.

To use an existing local production profile, point `BNS_STUDIO_PROFILE_DIR` at its runtime directory containing `bns_skeleton_bridge/` and `PakBridge/`. Set `BNS_STUDIO_PYTHON` to the Python executable used by that profile.
