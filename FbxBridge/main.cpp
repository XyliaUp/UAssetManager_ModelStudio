#define NOMINMAX
#include <fbxsdk.h>
#include <windows.h>
#include <algorithm>
#include <cmath>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <locale>
#include <map>
#include <memory>
#include <set>
#include <sstream>
#include <string>
#include <vector>
#ifdef BNS_HAS_PMX_IMPORT
#include "PmxImport.h"
#endif

using Path = std::filesystem::path;

struct MappingRow {
  std::string Target;
  double Scale;
};

struct Bone {
  FbxNode* Node;
  FbxNode* Parent;
};

struct GeometryFit {
  double Scale = 1.0;
  FbxVector4 Offset = FbxVector4(0.0, 0.0, 0.0);
};

static bool Fail(const std::string& Message) {
  std::cerr << Message << '\n';
  return false;
}

static void DestroyManager(FbxManager* Manager) {
  Manager->Destroy();
}

static std::string Utf8(const Path& Value) {
  return Value.u8string();
}

static FbxScene* Load(FbxManager* Manager, const Path& File, bool ReportLimits = true) {
#ifdef BNS_HAS_PMX_IMPORT
  if (_wcsicmp(File.extension().c_str(), L".pmx") == 0) {
	std::string Error;
	FbxScene* Scene = LoadPmx(Manager, File, Error, ReportLimits);
	if (!Scene) {
	  Fail("PMX import failed: " + Error);
	}
	return Scene;
  }
#endif
  FbxImporter* Importer = FbxImporter::Create(Manager, "");
  FbxScene* Scene = FbxScene::Create(Manager, "");
  const bool Loaded = Importer->Initialize(Utf8(File).c_str(), -1, Manager->GetIOSettings()) && Importer->Import(Scene);
  if (!Loaded) {
	Fail("FBX import failed: " + Utf8(File) + ": " + Importer->GetStatus().GetErrorString());
	Scene->Destroy();
	Scene = nullptr;
  }
  Importer->Destroy();
  return Scene;
}

static FbxSkin* Skin(FbxNode* Node) {
  return FbxCast<FbxSkin>(Node->GetMesh()->GetDeformer(0, FbxDeformer::eSkin));
}

static void Scan(FbxNode* Node, FbxNode* ParentBone, std::vector<Bone>& Bones, std::vector<FbxNode*>& Meshes) {
  if (Node->GetSkeleton()) {
	Bones.push_back({Node, ParentBone});
	ParentBone = Node;
  }
  if (Node->GetMesh() && Node->GetMesh()->GetDeformerCount(FbxDeformer::eSkin)) {
	Meshes.push_back(Node);
  }
  for (int Index = 0; Index < Node->GetChildCount(); ++Index) {
	Scan(Node->GetChild(Index), ParentBone, Bones, Meshes);
  }
}

static bool NameIsValid(const std::string& Value) {
  return !Value.empty() && Value.find_first_of("\t\r\n") == std::string::npos;
}

static FbxNode* GetMesh(FbxScene* Scene, std::vector<Bone>& Bones) {
  std::vector<FbxNode*> Meshes;
  Scan(Scene->GetRootNode(), nullptr, Bones, Meshes);
  if (Bones.empty() || Meshes.size() != 1 || Meshes.front()->GetMesh()->GetDeformerCount(FbxDeformer::eSkin) != 1) {
	Fail("FBX must have skeleton nodes and exactly one mesh with one skin.");
	return nullptr;
  }
  std::set<std::string> Names;
  for (const Bone& Entry : Bones) {
	const std::string Name = Entry.Node->GetName();
	if (!NameIsValid(Name) || !Names.insert(Name).second) {
	  Fail("Duplicate or invalid FBX bone name: " + Name);
	  return nullptr;
	}
  }
  return Meshes.front();
}

static bool PolygonMaterial(FbxNode* Node, int Polygon, int& Slot) {
  FbxGeometryElementMaterial* Material = Node->GetMesh()->GetElementMaterial(0);
  if (!Material) {
	Slot = Node->GetMaterialCount() == 1 ? 0 : -1;
	return Node->GetMaterialCount() <= 1 || Fail("Unsupported FBX material mapping.");
  }

  const auto Mode = Material->GetMappingMode();
  if (Mode != FbxLayerElement::eAllSame && Mode != FbxLayerElement::eByPolygon) {
	return Fail("Unsupported FBX material mapping mode.");
  }

  const int Index = Mode == FbxLayerElement::eAllSame ? 0 : Polygon;
  Slot = Index;
  if (Material->GetReferenceMode() != FbxLayerElement::eDirect) {
	if (Index >= Material->GetIndexArray().GetCount()) {
	  return Fail("Incomplete FBX material assignment.");
	}
	Slot = Material->GetIndexArray().GetAt(Index);
  }
  return Slot == -1 || (Slot >= 0 && Slot < Node->GetMaterialCount()) || Fail("FBX polygon refers to a missing material slot.");
}

static bool MaterialSlots(FbxNode* Node, std::vector<int>& Slots) {
  std::set<int> Used;
  for (int Polygon = 0; Polygon < Node->GetMesh()->GetPolygonCount(); ++Polygon) {
	int Slot;
	if (!PolygonMaterial(Node, Polygon, Slot)) {
	  return false;
	}
	if (Slot >= 0) {
	  Used.insert(Slot);
	}
  }
  Slots.assign(Used.begin(), Used.end());
  return true;
}

static FbxFileTexture* DiffuseTexture(FbxSurfaceMaterial* Material) {
  FbxProperty Property = Material->FindProperty(FbxSurfaceMaterial::sDiffuse);
  if (!Property.IsValid() || Property.GetSrcObjectCount<FbxFileTexture>() != 1 || Property.GetSrcObjectCount<FbxLayeredTexture>()) {
	return nullptr;
  }
  return Property.GetSrcObject<FbxFileTexture>();
}

static std::string Diffuse(FbxSurfaceMaterial* Material, const Path& Source) {
  FbxFileTexture* Texture = DiffuseTexture(Material);
  if (!Texture) {
	return {};
  }
  const std::string Relative = Texture->GetRelativeFileName();
  const std::string Absolute = Texture->GetFileName();
  std::error_code Error;
  if (!Relative.empty() && std::filesystem::is_regular_file(Source.parent_path() / std::filesystem::u8path(Relative), Error)) {
	return Utf8((Source.parent_path() / std::filesystem::u8path(Relative)).lexically_normal());
  }
  if (!Absolute.empty()) {
	return Absolute;
  }
  return Relative;
}

static bool WriteMaterialRows(std::ostream& Report, FbxNode* Node, const std::vector<int>& Slots, const Path& Source) {
  std::set<std::string> Names;
  for (const int Slot : Slots) {
	FbxSurfaceMaterial* Material = Node->GetMaterial(Slot);
	const std::string Name = Material->GetName();
	if (!NameIsValid(Name) || !Names.insert(Name).second) {
	  return Fail("Duplicate or invalid material name: " + Name);
	}
	const std::string File = Diffuse(Material, Source);
	const FbxProperty DiffuseColor = Material->FindProperty(FbxSurfaceMaterial::sDiffuse);
	const FbxProperty Transparency = Material->FindProperty(FbxSurfaceMaterial::sTransparencyFactor);
	const FbxDouble3 Color = DiffuseColor.IsValid() ? DiffuseColor.Get<FbxDouble3>() : FbxDouble3(1.0, 1.0, 1.0);
	const double Alpha = Transparency.IsValid() ? 1.0 - Transparency.Get<FbxDouble>() : 1.0;
	Report << "M\t" << Name << '\t' << (NameIsValid(File) ? File : "");
	for (const double Component : {Color[0], Color[1], Color[2], Alpha}) {
	  Report << '\t' << (std::isfinite(Component) ? std::clamp(Component, 0.0, 1.0) : 1.0);
	}
	Report << '\n';
  }
  return true;
}

static void WriteBoneRows(std::ostream& Report, const std::vector<Bone>& Bones) {
  for (const Bone& Entry : Bones) {
	Report << "B\t" << Entry.Node->GetName() << '\n';
  }
}

static bool IsPositiveSkinWeight(double Weight) {
  return std::isfinite(Weight) && Weight > 0.0;
}

static bool WriteWeightedBoneRows(std::ostream& Report, FbxSkin* MeshSkin, const std::vector<Bone>& Bones, bool IncludeHairWeights) {
  constexpr double HairWeightThreshold = 0.0001;
  std::set<std::string> WeightedBoneNames;
  std::set<FbxNode*> HairWeightedBones;
  for (int Index = 0; Index < MeshSkin->GetClusterCount(); ++Index) {
	FbxCluster* Cluster = MeshSkin->GetCluster(Index);
	FbxNode* Link = Cluster->GetLink();
	const double* Weights = Cluster->GetControlPointWeights();
	const int WeightCount = Cluster->GetControlPointIndicesCount();
	if (WeightCount > 0 && !Weights) {
	  return Fail("Skin cluster has no control point weights.");
	}
	double Maximum = 0.0;
	for (int WeightIndex = 0; WeightIndex < WeightCount; ++WeightIndex) {
	  const double Weight = Weights[WeightIndex];
	  if (IsPositiveSkinWeight(Weight)) {
		Maximum = std::max(Maximum, Weight);
		if (!IncludeHairWeights || Maximum > HairWeightThreshold) {
		  break;
		}
	  }
	}
	if (Maximum > 0.0) {
	  if (!Link) {
		return Fail("Weighted skin cluster has no linked bone.");
	  }
	  WeightedBoneNames.insert(Link->GetName());
	}
	if (IncludeHairWeights && Maximum > HairWeightThreshold) {
	  HairWeightedBones.insert(Link);
	}
  }

  Report << 'S';
  for (const Bone& Entry : Bones) {
	const char* Name = Entry.Node->GetName();
	if (WeightedBoneNames.erase(Name)) {
	  Report << '\t' << Name;
	}
  }
  Report << '\n';
  if (!WeightedBoneNames.empty()) {
	return Fail("Weighted skin cluster links a node outside the skeleton: " + *WeightedBoneNames.begin());
  }
  for (const Bone& Entry : Bones) {
	if (HairWeightedBones.count(Entry.Node)) {
	  Report << "W\t" << Entry.Node->GetName() << '\n';
	}
  }
  return true;
}

static bool Inspect(FbxScene* Scene, const Path& Source, const Path& Output) {
  std::vector<Bone> Bones;
  FbxNode* Node = GetMesh(Scene, Bones);
  std::vector<int> Slots;
  if (!Node || !MaterialSlots(Node, Slots)) {
	return false;
  }

  std::ofstream Report(Output, std::ios::binary);
  Report.imbue(std::locale::classic());
  Report << std::setprecision(17);
  WriteBoneRows(Report, Bones);
  if (!WriteMaterialRows(Report, Node, Slots, Source)) {
	return false;
  }
  FbxMesh* Mesh = Node->GetMesh();
  int Triangles = 0;
  for (int Index = 0; Index < Mesh->GetPolygonCount(); ++Index) {
	Triangles += Mesh->GetPolygonSize(Index) - 2;
  }
  Report << "V\t" << Mesh->GetControlPointsCount() << '\n';
  Report << "T\t" << Triangles << '\n';
  if (!WriteWeightedBoneRows(Report, Skin(Node), Bones, true)) {
	return false;
  }
  Report.close();
  return Report.good() || Fail("Could not write the FBX inspection report.");
}

static FbxAxisSystem UnrealFbxAxes() {
  const auto Front = static_cast<FbxAxisSystem::EFrontVector>(-FbxAxisSystem::eParityOdd);
  return FbxAxisSystem(FbxAxisSystem::eZAxis, Front, FbxAxisSystem::eRightHanded);
}

static FbxAMatrix MeshWorldTransform(FbxNode* Node) {
  FbxAMatrix Geometry;
  Geometry.SetT(Node->GetGeometricTranslation(FbxNode::eSourcePivot));
  Geometry.SetR(Node->GetGeometricRotation(FbxNode::eSourcePivot));
  Geometry.SetS(Node->GetGeometricScaling(FbxNode::eSourcePivot));
  return Node->EvaluateGlobalTransform() * Geometry;
}

static bool WriteTriangleUVs(std::ostream& Report, FbxMesh* Mesh, int Polygon, const std::string& UVSet) {
  // Report bottom-origin FBX UVs in the same order as the mirrored triangle.
  FbxVector2 Corners[3];
  const int Indices[] = {0, 2, 1};
  for (int Index = 0; Index < 3; ++Index) {
	bool Unmapped = false;
	if (UVSet.empty() || !Mesh->GetPolygonVertexUV(Polygon, Indices[Index], UVSet.c_str(), Corners[Index], Unmapped) || Unmapped
		|| !std::isfinite(Corners[Index][0]) || !std::isfinite(Corners[Index][1])) {
	  Report << "\t-\t-\t-\t-\t-\t-";
	  return Report.good();
	}
  }
  for (const FbxVector2& UV : Corners) {
	Report << '\t' << UV[0] << '\t' << UV[1];
  }
  return Report.good();
}

static bool Preview(FbxManager* Manager, FbxScene* Scene, const Path& Source, const Path& Output) {
  std::vector<Bone> Bones;
  FbxNode* Node = GetMesh(Scene, Bones);
  std::vector<int> Slots;
  if (!Node || !MaterialSlots(Node, Slots)) {
	return false;
  }

  std::ofstream Report(Output, std::ios::binary);
  Report.imbue(std::locale::classic());
  Report << std::setprecision(17);
  WriteBoneRows(Report, Bones);
  if (!WriteWeightedBoneRows(Report, Skin(Node), Bones, false)) {
	return false;
  }

  // Match UE4.25's FBX conversion, then mirror the FBX Y axis as UE does.
  UnrealFbxAxes().ConvertScene(Scene);
  FbxSystemUnit::cm.ConvertScene(Scene);
  FbxGeometryConverter Converter(Manager);
  if (!Converter.Triangulate(Scene, true)) {
	return Fail("Could not triangulate the FBX preview mesh.");
  }

  const FbxAMatrix World = MeshWorldTransform(Node);
  FbxMesh* Mesh = Node->GetMesh();
  if (!WriteMaterialRows(Report, Node, Slots, Source)) {
	return false;
  }
  FbxStringList UVSets;
  Mesh->GetUVSetNames(UVSets);
  const std::string DefaultUV = UVSets.GetCount() ? UVSets.GetStringAt(0) : "";
  std::map<int, int> SlotIndices;
  std::vector<std::string> MaterialUVs;
  for (const int Slot : Slots) {
	SlotIndices.emplace(Slot, static_cast<int>(MaterialUVs.size()));
	const FbxFileTexture* Texture = DiffuseTexture(Node->GetMaterial(Slot));
	const std::string RequestedUV = Texture ? Texture->UVSet.Get().Buffer() : "";
	std::string Set = DefaultUV;
	for (int UVIndex = 0; UVIndex < UVSets.GetCount(); ++UVIndex) {
	  if (RequestedUV == UVSets.GetStringAt(UVIndex)) {
		Set = RequestedUV;
		break;
	  }
	}
	MaterialUVs.push_back(Set);
  }
  const int PointCount = Mesh->GetControlPointsCount();
  for (int Index = 0; Index < PointCount; ++Index) {
	const FbxVector4 Point = World.MultT(Mesh->GetControlPointAt(Index));
	if (!std::isfinite(Point[0]) || !std::isfinite(Point[1]) || !std::isfinite(Point[2])) {
	  return Fail("FBX preview contains a non-finite position.");
	}
	Report << "P\t" << Point[0] << '\t' << -Point[1] << '\t' << Point[2] << '\n';
  }
  for (int Polygon = 0; Polygon < Mesh->GetPolygonCount(); ++Polygon) {
	if (Mesh->GetPolygonSize(Polygon) != 3) {
	  return Fail("FBX preview contains a non-triangular polygon.");
	}
	const int A = Mesh->GetPolygonVertex(Polygon, 0);
	const int B = Mesh->GetPolygonVertex(Polygon, 1);
	const int C = Mesh->GetPolygonVertex(Polygon, 2);
	if (A < 0 || A >= PointCount || B < 0 || B >= PointCount || C < 0 || C >= PointCount) {
	  return Fail("FBX preview contains an invalid triangle index.");
	}
	int Slot;
	if (!PolygonMaterial(Node, Polygon, Slot)) {
	  return false;
	}
	const auto Assigned = SlotIndices.find(Slot);
	if (Slot >= 0 && Assigned == SlotIndices.end()) {
	  return Fail("Triangulation changed the FBX material slots.");
	}
	const int MaterialIndex = Slot >= 0 ? Assigned->second : -1;
	const std::string& UVSet = MaterialIndex >= 0 ? MaterialUVs[MaterialIndex] : DefaultUV;
	Report << "F\t" << A << '\t' << C << '\t' << B << '\t' << MaterialIndex;
	if (!WriteTriangleUVs(Report, Mesh, Polygon, UVSet)) {
	  return Fail("Could not write FBX triangle UVs.");
	}
	Report << '\n';
  }
  Report.close();
  return Report.good() || Fail("Could not write the FBX preview report.");
}

static bool ReadMapping(const Path& File, std::map<std::string, MappingRow>& Mapping) {
  std::ifstream Input(File, std::ios::binary);
  if (!Input) {
	return Fail("Could not open FBX mapping TSV.");
  }
  std::string Line;
  bool First = true;
  while (std::getline(Input, Line)) {
	if (First && Line.compare(0, 3, "\xEF\xBB\xBF") == 0) {
	  Line.erase(0, 3);
	}
	First = false;
	if (!Line.empty() && Line.back() == '\r') {
	  Line.pop_back();
	}
	if (Line.empty()) {
	  continue;
	}
	const size_t Start = Line.find('\t');
	if (Start == std::string::npos) {
	  return Fail("Mapping row requires source and target.");
	}
	const size_t ScaleColumn = Line.find('\t', Start + 1);
	const std::string From = Line.substr(0, Start);
	const std::string To = Line.substr(Start + 1, ScaleColumn == std::string::npos ? ScaleColumn : ScaleColumn - Start - 1);
	double Scale = 1.0;
	if (ScaleColumn != std::string::npos) {
	  const char* StartNumber = Line.c_str() + ScaleColumn + 1;
	  char* EndNumber = nullptr;
	  Scale = std::strtod(StartNumber, &EndNumber);
	  if (StartNumber == EndNumber || *EndNumber || !std::isfinite(Scale) || Scale <= 0.0) {
		return Fail("Invalid mapping weight scale for " + From);
	  }
	}
	if (!NameIsValid(From) || !NameIsValid(To) || !Mapping.emplace(From, MappingRow{To, Scale}).second) {
	  return Fail("Duplicate or invalid FBX mapping row: " + From);
	}
  }
  return !Mapping.empty() || Fail("FBX mapping TSV is empty.");
}

static bool Near(const FbxAMatrix& Left, const FbxAMatrix& Right) {
  for (int Row = 0; Row < 4; ++Row) {
	for (int Column = 0; Column < 4; ++Column) {
	  if (std::abs(Left.Get(Row, Column) - Right.Get(Row, Column)) > 0.0001) {
		return false;
	  }
	}
  }
  return true;
}

static bool CollectBind(FbxSkin* DonorSkin, std::map<FbxNode*, FbxAMatrix>& Bind) {
  for (int Index = 0; Index < DonorSkin->GetClusterCount(); ++Index) {
	FbxCluster* Cluster = DonorSkin->GetCluster(Index);
	if (!Cluster->GetLink()) {
	  return Fail("Donor cluster has no linked bone.");
	}
	FbxAMatrix Value;
	Cluster->GetTransformLinkMatrix(Value);
	const auto Inserted = Bind.emplace(Cluster->GetLink(), Value);
	if (!Inserted.second && !Near(Inserted.first->second, Value)) {
	  return Fail("Donor has conflicting bind matrices.");
	}
  }
  return !Bind.empty() || Fail("Donor skin has no bind matrices.");
}

static bool GetWeights(FbxNode* Node, const std::map<std::string, MappingRow>& Mapping, const std::map<std::string, int>& Targets, std::vector<std::map<int, double>>& Weights, FbxAMatrix& MeshBind) {
  FbxSkin* SourceSkin = Skin(Node);
  if (SourceSkin->GetSkinningType() != FbxSkin::eLinear && SourceSkin->GetSkinningType() != FbxSkin::eRigid) {
	return Fail("Only linear and rigid FBX skinning are supported.");
  }

  bool HasBind = false;
  Weights.resize(Node->GetMesh()->GetControlPointsCount());
  for (int Index = 0; Index < SourceSkin->GetClusterCount(); ++Index) {
	FbxCluster* Cluster = SourceSkin->GetCluster(Index);
	if (!Cluster->GetLink() || Cluster->GetLinkMode() == FbxCluster::eAdditive) {
	  return Fail("Unsupported source skin cluster.");
	}
	FbxAMatrix Matrix;
	Cluster->GetTransformMatrix(Matrix);
	if (HasBind && !Near(MeshBind, Matrix)) {
	  return Fail("Source skin has conflicting mesh bind transforms.");
	}
	MeshBind = Matrix;
	HasBind = true;
	const std::string Name = Cluster->GetLink()->GetName();
	const auto Mapped = Mapping.find(Name);
	for (int WeightIndex = 0; WeightIndex < Cluster->GetControlPointIndicesCount(); ++WeightIndex) {
	  const double Weight = Cluster->GetControlPointWeights()[WeightIndex];
	  if (Weight == 0) {
		continue;
	  }
	  if (!IsPositiveSkinWeight(Weight)) {
		return Fail("Invalid source skin weight for " + Name);
	  }
	  if (Mapped == Mapping.end()) {
		return Fail("Weighted source bone has no mapping: " + Name);
	  }
	  const auto Target = Targets.find(Mapped->second.Target);
	  if (Target == Targets.end()) {
		return Fail("Target bone does not exist: " + Mapped->second.Target);
	  }
	  const int Point = Cluster->GetControlPointIndices()[WeightIndex];
	  if (Point < 0 || Point >= static_cast<int>(Weights.size())) {
		return Fail("Invalid source skin control point index.");
	  }
	  Weights[Point][Target->second] += Weight * Mapped->second.Scale;
	}
  }

  for (auto& Point : Weights) {
	if (Point.empty() || Point.size() > 8) {
	  return Fail("Each control point must have between one and eight merged influences.");
	}
	double Total = 0.0;
	for (const auto& Weight : Point) {
	  Total += Weight.second;
	}
	if (!std::isfinite(Total) || Total <= 0.0) {
	  return Fail("Scaled skin weights overflowed or vanished.");
	}
	for (auto& Weight : Point) {
	  Weight.second /= Total;
	}
  }
  return HasBind || Fail("Source skin has no bind matrix.");
}

static bool HasMesh(FbxNode* Node) {
  if (Node->GetMesh()) {
	return true;
  }
  for (int Index = 0; Index < Node->GetChildCount(); ++Index) {
	if (HasMesh(Node->GetChild(Index))) {
	  return true;
	}
  }
  return false;
}

static bool Save(FbxManager* Manager, FbxScene* Scene, const Path& Output) {
  FbxExporter* Exporter = FbxExporter::Create(Manager, "");
  const int Format = Manager->GetIOPluginRegistry()->GetNativeWriterFormat();
  const bool Saved = Exporter->Initialize(Utf8(Output).c_str(), Format, Manager->GetIOSettings()) && Exporter->Export(Scene);
  if (!Saved) {
	Fail("FBX export failed: " + std::string(Exporter->GetStatus().GetErrorString()));
  }
  Exporter->Destroy();
  return Saved;
}

static bool ReadTarget(FbxScene* Donor, const Path& File, std::vector<Bone>& Bones, std::map<FbxNode*, FbxAMatrix>& Bind) {
  std::ifstream Input(File, std::ios::binary);
  if (!Input) {
	return Fail("Could not open target skeleton TSV.");
  }
  Donor->GetGlobalSettings().SetAxisSystem(UnrealFbxAxes());
  Donor->GetGlobalSettings().SetSystemUnit(FbxSystemUnit::cm);
  std::set<std::string> Names;
  std::string Line;
  while (std::getline(Input, Line)) {
	if (Bones.empty() && Line.compare(0, 3, "\xEF\xBB\xBF") == 0) {
	  Line.erase(0, 3);
	}
	if (Line.empty() || Line == "\r") {
	  continue;
	}
	const size_t Split = Line.find('\t');
	const std::string Name = Line.substr(0, Split);
	if (Split == std::string::npos || !NameIsValid(Name) || !Names.insert(Name).second) {
	  return Fail("Invalid or duplicate target bone name: " + Name);
	}
	std::istringstream Fields(Line.substr(Split + 1));
	Fields.imbue(std::locale::classic());
	int Parent;
	if (!(Fields >> Parent) || Parent < -1 || Parent >= static_cast<int>(Bones.size())) {
	  return Fail("Target parent must precede the child: " + Name);
	}
	double Number[10];
	for (double& Value : Number) {
	  if (!(Fields >> Value) || !std::isfinite(Value)) {
		return Fail("Invalid target transform: " + Name);
	  }
	}
	std::string Extra;
	if (Fields >> Extra) {
	  return Fail("Unexpected target transform field: " + Name);
	}
	const double Length = std::sqrt(Number[3] * Number[3] + Number[4] * Number[4] + Number[5] * Number[5] + Number[6] * Number[6]);
	if (!std::isfinite(Length) || Length < 0.000001 || Number[7] <= 0 || Number[8] <= 0 || Number[9] <= 0) {
	  return Fail("Invalid target quaternion or scale: " + Name);
	}
	const FbxVector4 Translation(Number[0], -Number[1], Number[2]);
	const FbxQuaternion Rotation(-Number[3] / Length, Number[4] / Length, -Number[5] / Length, Number[6] / Length);
	const FbxVector4 Scale(Number[7], Number[8], Number[9]);
	FbxAMatrix Local;
	Local.SetTQS(Translation, Rotation, Scale);
	FbxNode* ParentNode = Parent >= 0 ? Bones[Parent].Node : nullptr;
	FbxNode* Node = FbxNode::Create(Donor, Name.c_str());
	Bones.push_back({Node, ParentNode});
	Bind.emplace(Node, ParentNode ? Bind.at(ParentNode) * Local : Local);
  }
  return Input.eof() && !Bones.empty() || Fail("Could not read target skeleton TSV rows.");
}

static bool TransformPoints(FbxGeometryBase* Geometry, const FbxAMatrix& World, const FbxAMatrix& Inverse, const GeometryFit& Fit) {
  for (int Index = 0; Index < Geometry->GetControlPointsCount(); ++Index) {
	const FbxVector4 Point = World.MultT(Geometry->GetControlPointAt(Index));
	const FbxVector4 Fitted(Point[0] * Fit.Scale + Fit.Offset[0], Point[1] * Fit.Scale - Fit.Offset[1], Point[2] * Fit.Scale + Fit.Offset[2]);
	const FbxVector4 Local = Inverse.MultT(Fitted);
	if (!std::isfinite(Local[0]) || !std::isfinite(Local[1]) || !std::isfinite(Local[2])) {
	  return Fail("Geometry fitting produced a non-finite control point.");
	}
	Geometry->SetControlPointAt(Local, Index);
  }
  return true;
}

static bool ApplyGeometryFit(FbxScene* Source, FbxNode* Node, const GeometryFit& Fit) {
  if (Fit.Scale == 1.0 && Fit.Offset[0] == 0.0 && Fit.Offset[1] == 0.0 && Fit.Offset[2] == 0.0) {
	return true;
  }
  UnrealFbxAxes().ConvertScene(Source);
  FbxSystemUnit::cm.ConvertScene(Source);
  const FbxAMatrix World = MeshWorldTransform(Node);
  const FbxVector4 WorldScale = World.GetS();
  for (int Axis = 0; Axis < 3; ++Axis) {
	if (!std::isfinite(WorldScale[Axis]) || std::abs(WorldScale[Axis]) < 0.000001) {
	  return Fail("Geometry fitting requires an invertible mesh transform.");
	}
  }
  const FbxAMatrix Inverse = World.Inverse();
  FbxMesh* Mesh = Node->GetMesh();
  if (!TransformPoints(Mesh, World, Inverse, Fit)) {
	return false;
  }
  std::set<FbxShape*> Shapes;
  for (int ShapeIndex = 0; ShapeIndex < Mesh->GetDeformerCount(FbxDeformer::eBlendShape); ++ShapeIndex) {
	auto* Blend = FbxCast<FbxBlendShape>(Mesh->GetDeformer(ShapeIndex, FbxDeformer::eBlendShape));
	for (int ChannelIndex = 0; ChannelIndex < Blend->GetBlendShapeChannelCount(); ++ChannelIndex) {
	  FbxBlendShapeChannel* Channel = Blend->GetBlendShapeChannel(ChannelIndex);
	  for (int TargetIndex = 0; TargetIndex < Channel->GetTargetShapeCount(); ++TargetIndex) {
		FbxShape* Shape = Channel->GetTargetShape(TargetIndex);
		if (Shapes.insert(Shape).second && !TransformPoints(Shape, World, Inverse, Fit)) {
		  return false;
		}
	  }
	}
  }
  return true;
}

static bool ApplyDiffuseTextures(FbxScene* Scene, FbxNode* Node, const Path& Output, const Path& TextureFile) {
  std::ifstream Input(TextureFile, std::ios::binary);
  std::vector<int> Slots;
  if (!Input || !MaterialSlots(Node, Slots)) {
	return Fail("Could not read the FBX texture links.");
  }
  std::set<int> Seen;
  std::string Line;
  while (std::getline(Input, Line)) {
	if (!Line.empty() && Line.back() == '\r') {
	  Line.pop_back();
	}
	const size_t Split = Line.find('\t');
	if (Split == std::string::npos) {
	  return Fail("Texture link row requires a slot and a file.");
	}
	std::istringstream Index(Line.substr(0, Split));
	unsigned int Slot;
	if (!(Index >> Slot) || Index.peek() != EOF || Slot >= Slots.size() || !Seen.insert(Slot).second) {
	  return Fail("Invalid or duplicate FBX texture slot.");
	}
	const Path Absolute = std::filesystem::u8path(Line.substr(Split + 1)).lexically_normal();
	const Path Relative = Absolute.lexically_relative(Output.parent_path());
	std::error_code Error;
	if (!Absolute.is_absolute() || Relative.empty() || *Relative.begin() == L".." || !std::filesystem::is_regular_file(Absolute, Error)) {
	  return Fail("FBX texture must be a file under the export directory.");
	}
	FbxSurfaceMaterial* Material = Node->GetMaterial(Slots[Slot]);
	FbxProperty Diffuse = Material->FindProperty(FbxSurfaceMaterial::sDiffuse);
	if (!Diffuse.IsValid()) {
	  return Fail("Material has no FBX diffuse channel: " + std::string(Material->GetName()));
	}
	FbxFileTexture* Existing = DiffuseTexture(Material);
	FbxFileTexture* Texture = Existing ? FbxCast<FbxFileTexture>(Existing->Clone(FbxObject::eDeepClone, Scene)) : FbxFileTexture::Create(Scene, "Diffuse");
	if (!Texture || !Texture->SetFileName(Utf8(Absolute).c_str()) || !Texture->SetRelativeFileName(Utf8(Relative).c_str())) {
	  return Fail("Could not create the FBX diffuse texture link: " + Utf8(Absolute));
	}
	if (!Existing) {
	  Texture->SetMaterialUse(FbxFileTexture::eModelMaterial);
	  Texture->SetTextureUse(FbxTexture::eStandard);
	  Texture->SetMappingType(FbxTexture::eUV);
	}
	Diffuse.DisconnectAllSrcObject<FbxTexture>();
	if (!Diffuse.ConnectSrcObject(Texture)) {
	  return Fail("Could not connect the FBX diffuse texture.");
	}
  }
  return true;
}

static bool Remap(FbxManager* Manager, FbxScene* Source, FbxScene* Donor, const Path& TargetFile, const Path& MappingFile, const Path& Output, const GeometryFit& Fit, const Path& TextureFile) {
  std::vector<Bone> SourceBones;
  std::vector<Bone> DonorBones;
  FbxNode* SourceMesh = GetMesh(Source, SourceBones);
  if (!SourceMesh) {
	return false;
  }
  if (!ApplyGeometryFit(Source, SourceMesh, Fit)) {
	return false;
  }
  std::map<FbxNode*, FbxAMatrix> Bind;
  if (_wcsicmp(TargetFile.extension().c_str(), L".tsv") == 0) {
	if (!ReadTarget(Donor, TargetFile, DonorBones, Bind)) {
	  return false;
	}
  } else {
	FbxNode* DonorMesh = GetMesh(Donor, DonorBones);
	if (!DonorMesh || !CollectBind(Skin(DonorMesh), Bind)) {
	  return false;
	}
	for (const Bone& Entry : DonorBones) {
	  Bind.try_emplace(Entry.Node, Entry.Node->EvaluateGlobalTransform());
	}
  }
  Donor->GetGlobalSettings().GetAxisSystem().ConvertScene(Source);
  Donor->GetGlobalSettings().GetSystemUnit().ConvertScene(Source);

  std::map<std::string, MappingRow> Mapping;
  if (!ReadMapping(MappingFile, Mapping)) {
	return false;
  }
  std::map<std::string, int> Targets;
  int Roots = 0;
  for (const Bone& Entry : DonorBones) {
	Targets.emplace(Entry.Node->GetName(), static_cast<int>(Targets.size()));
	if (!Entry.Parent) {
	  ++Roots;
	}
  }
  if (Roots != 1) {
	return Fail("Donor must have exactly one skeletal root.");
  }
  std::vector<std::map<int, double>> Weights;
  FbxAMatrix MeshBind;
  if (!GetWeights(SourceMesh, Mapping, Targets, Weights, MeshBind)) {
	return false;
  }
  for (const Bone& Entry : SourceBones) {
	if (!Entry.Parent && HasMesh(Entry.Node)) {
	  return Fail("FBX meshes must not be parented directly under source bones.");
	}
  }

  FbxMesh* Geometry = SourceMesh->GetMesh();
  for (int Index = 0; Index < Geometry->GetDeformerCount(); ++Index) {
	if (Geometry->GetDeformer(Index)->GetDeformerType() == FbxDeformer::eSkin) {
	  Geometry->RemoveDeformer(Index)->Destroy();
	  break;
	}
  }
  while (Source->GetPoseCount()) {
	FbxPose* OldPose = Source->GetPose(0);
	Source->RemovePose(0);
	OldPose->Destroy();
  }
  for (const Bone& Entry : SourceBones) {
	if (!Entry.Parent) {
	  Entry.Node->Destroy(true);
	}
  }

  auto* NewSkin = FbxSkin::Create(Source, "BnsSkin");
  NewSkin->SetSkinningType(FbxSkin::eLinear);
  auto* Pose = FbxPose::Create(Source, "BnsBindPose");
  Pose->SetIsBindPose(true);
  std::map<FbxNode*, FbxNode*> NewNodes;
  std::vector<FbxCluster*> NewClusters;
  for (const Bone& Entry : DonorBones) {
	FbxNode* NewNode = FbxNode::Create(Source, Entry.Node->GetName());
	FbxSkeleton* Skeleton = FbxSkeleton::Create(Source, Entry.Node->GetName());
	Skeleton->SetSkeletonType(Entry.Parent ? FbxSkeleton::eLimbNode : FbxSkeleton::eRoot);
	NewNode->SetNodeAttribute(Skeleton);
	FbxNode* NewParent = Entry.Parent ? NewNodes.at(Entry.Parent) : Source->GetRootNode();
	NewParent->AddChild(NewNode);
	FbxAMatrix Global = Bind.at(Entry.Node);
	FbxAMatrix Local = Entry.Parent ? Bind.at(Entry.Parent).Inverse() * Global : Global;
	NewNode->LclTranslation.Set(Local.GetT());
	NewNode->LclRotation.Set(Local.GetR());
	NewNode->LclScaling.Set(Local.GetS());
	if (!Near(NewNode->EvaluateGlobalTransform(), Global)) {
	  return Fail("Donor bind transform cannot be represented without affine shear: " + std::string(Entry.Node->GetName()));
	}
	NewNodes.emplace(Entry.Node, NewNode);
	Pose->Add(NewNode, FbxMatrix(Global));
	FbxCluster* Cluster = FbxCluster::Create(Source, Entry.Node->GetName());
	Cluster->SetLink(NewNode);
	Cluster->SetLinkMode(FbxCluster::eNormalize);
	Cluster->SetTransformMatrix(MeshBind);
	Cluster->SetTransformLinkMatrix(Global);
	NewSkin->AddCluster(Cluster);
	NewClusters.push_back(Cluster);
  }
  for (int Point = 0; Point < static_cast<int>(Weights.size()); ++Point) {
	for (const auto& Weight : Weights[Point]) {
	  NewClusters[Weight.first]->AddControlPointIndex(Point, Weight.second);
	}
  }
  for (FbxNode* Node = SourceMesh->GetParent(); Node && Node != Source->GetRootNode(); Node = Node->GetParent()) {
	Pose->Add(Node, FbxMatrix(Node->EvaluateGlobalTransform()));
  }
  Pose->Add(SourceMesh, FbxMatrix(MeshBind));
  Source->AddPose(Pose);
  Geometry->AddDeformer(NewSkin);
  if (!TextureFile.empty() && !ApplyDiffuseTextures(Source, SourceMesh, Output, TextureFile)) {
	return false;
  }
  return Save(Manager, Source, Output);
}

static bool ReadFit(wchar_t** Arguments, GeometryFit& Fit) {
  double Values[4];
  for (int Index = 0; Index < 4; ++Index) {
	const wchar_t* Start = Arguments[Index];
	wchar_t* End = nullptr;
	Values[Index] = std::wcstod(Start, &End);
	if (Start == End || *End || !std::isfinite(Values[Index])) {
	  return Fail("Geometry fit requires finite scale and offset values.");
	}
  }
  if (Values[0] <= 0.0) {
	return Fail("Geometry fit requires a positive uniform scale.");
  }
  Fit.Scale = Values[0];
  Fit.Offset = FbxVector4(Values[1], Values[2], Values[3]);
  return true;
}

int wmain(int Count, wchar_t** Arguments) {
  const std::wstring Command = Count > 1 ? Arguments[1] : L"";
  if (((Command != L"inspect" && Command != L"preview") || Count != 4) && (Command != L"remap" || (Count != 6 && Count != 10 && Count != 11))) {
	Fail("Usage: BnsOutfitFbx inspect|preview input output.tsv | remap source target mapping.tsv output.fbx [scale xCm yCm zCm [textures.tsv]]");
	return 2;
  }
  GeometryFit Fit;
  if (Count >= 10 && !ReadFit(Arguments + 6, Fit)) {
	return 2;
  }
  std::unique_ptr<FbxManager, decltype(&DestroyManager)> Manager(FbxManager::Create(), DestroyManager);
  if (!Manager) {
	return 1;
  }
  Manager->SetIOSettings(FbxIOSettings::Create(Manager.get(), IOSROOT));
  Manager->GetIOSettings()->SetBoolProp(IMP_FBX_EXTRACT_EMBEDDED_DATA, false);
  Manager->GetIOSettings()->SetBoolProp(EXP_FBX_EMBEDDED, false);
  FbxScene* Source = Load(Manager.get(), Arguments[2], Command == L"remap");
  if (!Source) {
	return 1;
  }
  if (Command == L"inspect") {
	return Inspect(Source, Arguments[2], Arguments[3]) ? 0 : 1;
  }
  if (Command == L"preview") {
	return Preview(Manager.get(), Source, Arguments[2], Arguments[3]) ? 0 : 1;
  }
  const Path TargetFile = Arguments[3];
  FbxScene* Donor = _wcsicmp(TargetFile.extension().c_str(), L".tsv") == 0 ? FbxScene::Create(Manager.get(), "Target") : Load(Manager.get(), TargetFile);
  const Path TextureFile = Count == 11 ? Arguments[10] : L"";
  return Donor && Remap(Manager.get(), Source, Donor, TargetFile, Arguments[4], Arguments[5], Fit, TextureFile) ? 0 : 1;
}
