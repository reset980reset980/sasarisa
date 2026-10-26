"""Create the source archive, file manifest, and deterministic WiX components."""
import argparse
import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET
import zipfile
import uuid

APP_ID = "7f114a1b-b332-4d79-98b4-fe4092418d4d"
NS = "http://wixtoolset.org/schemas/v4/wxs"
ET.register_namespace("", NS)


def ident(prefix, value):
    return prefix + hashlib.sha256(value.encode("utf-8")).hexdigest()[:30]


def package(stage, root, version, wix):
    source = stage / "Sasarisa-Windows-Source.zip"
    with zipfile.ZipFile(source, "w", zipfile.ZIP_DEFLATED) as archive:
        archive.write(root.parent / "LICENSE", "LICENSE")
        paths = list(root.glob("*.ps1")) + list(root.glob("*.cmd")) + list(root.glob("*.md"))
        paths += list((root / "runtime").glob("*.py")) + list((root / "runtime").glob("*.txt"))
        paths += list((root / "poc").glob("*.py")) + list((root / "poc").glob("*.ps1")) + list((root / "poc").glob("*.txt"))
        paths += list((root / "installer").glob("*.wxs")) + list((root / "installer").glob("*.wixproj"))
        paths += [p for p in (root / "app").rglob("*") if p.is_file() and not {"bin", "obj"}.intersection(p.relative_to(root).parts) and (p.suffix in {".cs", ".xaml", ".csproj", ".manifest", ".ico"} or p.name.endswith("-LICENSE.txt"))]
        for path in sorted(set(paths)):
            archive.write(path, "windows/" + path.relative_to(root).as_posix())
    (stage / "THIRD-PARTY-NOTICES.md").write_text(
        "# Sasarisa Windows distribution\n\n"
        "Community Windows port of https://github.com/mvcompany8500-web/sasarisa\n"
        "Windows source: Sasarisa-Windows-Source.zip (AGPL-3.0, see LICENSE.txt).\n\n"
        "Python 3.13.17: https://www.python.org/; license: python/LICENSE.txt.\n"
        ".NET 10 runtime: https://github.com/dotnet/runtime; MIT license.\n"
        "WPF: https://github.com/dotnet/wpf; MIT license.\n"
        "WebView2 SDK: https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4314.35.\n"
        "NAudio 3.3.0: https://github.com/naudio/NAudio; MIT license: NAudio-LICENSE.txt.\n"
        "ModelContextProtocol.Core 2.3.0: https://github.com/modelcontextprotocol/csharp-sdk; Apache-2.0 with legacy MIT contributions; see ModelContextProtocol-LICENSE.txt.\n"
        "Microsoft.Extensions dependencies: https://github.com/dotnet/extensions; MIT license: MicrosoftExtensions-LICENSE.txt.\n"
        "Python dependencies and their licenses/metadata are included under python/Lib/site-packages/*.dist-info.\n"
        "These include sherpa-onnx 2.0.0, sherpa-onnx-core 2.0.0, sentencepiece 0.2.2, sounddevice 0.5.6, soundfile 0.14.0, numpy 2.4.6, cffi 2.1.1, pycparser 3.12, and typing_extensions 4.16.0.\n"
        "MSI is built with WiX Toolset 4.0.6: https://github.com/wixtoolset/wix (MS-RL).\n\n"
        "Korean model weights are downloaded separately from the k2-fsa model repository during first setup; they are not included in this MSI.\n"
        "https://huggingface.co/k2-fsa/sherpa-onnx-streaming-zipformer-korean-2024-06-16\n",
        encoding="utf-8",
    )
    files = []
    for path in sorted(stage.rglob("*")):
        if path.is_file() and path.name != "distribution.json":
            with path.open("rb") as stream:
                digest = hashlib.file_digest(stream, "sha256").hexdigest()
            files.append({"Path": path.relative_to(stage).as_posix(), "Size": path.stat().st_size, "Sha256": digest})
    (stage / "distribution.json").write_text(json.dumps({"ProductId": APP_ID, "Version": version, "Files": files}, indent=2), encoding="utf-8")
    files.append({"Path": "distribution.json"})
    doc = ET.Element(f"{{{NS}}}Wix")
    fragment = ET.SubElement(doc, "Fragment")
    directory_ref = ET.SubElement(fragment, "DirectoryRef", Id="INSTALLFOLDER")
    directories = {"": (directory_ref, "INSTALLFOLDER")}
    group = ET.SubElement(fragment, "ComponentGroup", Id="ApplicationFiles")
    used_folders = set()
    for item in files:
        path = Path(item["Path"])
        parent = ""
        for part in path.parts[:-1]:
            relative = parent + "/" + part if parent else part
            if relative not in directories:
                directory_id = ident("D", relative)
                node = ET.SubElement(directories[parent][0], "Directory", Id=directory_id, Name=part)
                directories[relative] = (node, directory_id)
            parent = relative
        component_id = ident("C", item["Path"])
        component = ET.SubElement(group, "Component", Id=component_id, Directory=directories[parent][1], Guid=str(uuid.uuid5(uuid.UUID(APP_ID), item["Path"].lower())), Bitness="always64")
        file_id = "MainExecutable" if item["Path"] == "VoiceAssistant.exe" else ident("F", item["Path"])
        ET.SubElement(component, "File", Id=file_id, Source="$(var.PayloadDir)\\" + item["Path"].replace("/", "\\"))
        ET.SubElement(component, "RegistryValue", Root="HKCU", Key="Software\\SasarisaWindows\\Components", Name=component_id, Type="integer", Value="1", KeyPath="yes")
        if parent not in used_folders:
            ET.SubElement(component, "RemoveFolder", Id=ident("R", parent), Directory=directories[parent][1], On="uninstall")
            used_folders.add(parent)
    # Intermediate directories may contain only child folders. Remove those too,
    # but never the shared LocalAppData/Programs parent outside INSTALLFOLDER.
    for relative, (_, directory_id) in directories.items():
        if relative not in used_folders:
            ET.SubElement(component, "RemoveFolder", Id=ident("R", relative), Directory=directory_id, On="uninstall")
    wix.parent.mkdir(parents=True, exist_ok=True)
    ET.indent(doc)
    ET.ElementTree(doc).write(wix, encoding="utf-8", xml_declaration=True)
    print(f"Prepared {len(files)} installer files and corresponding source archive")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--stage", type=Path, required=True)
    parser.add_argument("--windows-root", type=Path, required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--wix", type=Path, required=True)
    args = parser.parse_args()
    package(args.stage, args.windows_root, args.version, args.wix)
