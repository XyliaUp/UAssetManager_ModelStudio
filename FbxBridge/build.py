"""Build the Win64 FBX bridge in a Visual Studio x64 developer terminal."""

import argparse
import filecmp
from pathlib import Path
import shutil
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ue-root", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parent
    output = args.output.resolve() if args.output else root / "bin"
    sdk = args.ue_root.resolve() / "Engine/Source/ThirdParty/FBX/2018.1.1"
    lib = sdk / "lib/vs2015/x64/release"
    compiler = shutil.which("cl.exe")
    if not compiler:
        parser.error("Run from a Visual Studio x64 developer terminal.")
    if not (sdk / "include/fbxsdk.h").is_file() or not (lib / "libfbxsdk.lib").is_file():
        parser.error("The UE4.25 Win64 FBX SDK installation is required.")
    output.mkdir(parents=True, exist_ok=True)
    sources = [str(root / "main.cpp")]
    pmx_define = []
    if (root / "PmxImport.cpp").is_file():
        sources.append(str(root / "PmxImport.cpp"))
        pmx_define.append("/DBNS_HAS_PMX_IMPORT")
    subprocess.run([
        compiler, "/nologo", "/MD", "/O2", "/EHsc", "/std:c++17", "/utf-8", "/DFBXSDK_SHARED",
        "/I" + str(sdk / "include"), *pmx_define, *sources, str(lib / "libfbxsdk.lib"), "/link", "/MACHINE:X64", "/OUT:BnsOutfitFbx.exe",
    ], cwd=output, check=True)
    sdk_binary = lib / "libfbxsdk.dll"
    local_binary = output / sdk_binary.name
    if not local_binary.exists() or not filecmp.cmp(sdk_binary, local_binary, shallow=False):
        shutil.copy2(sdk_binary, local_binary)
    print(output / "BnsOutfitFbx.exe")


if __name__ == "__main__":
    main()
