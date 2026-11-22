#include "PmxImport.h"

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <Windows.h>
#include <array>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <fstream>
#include <set>
#include <vector>

struct PmxVertex {
  FbxVector4 position;
  FbxVector4 normal;
  FbxVector2 uv;
  std::array<int, 4> bones{-1, -1, -1, -1};
  std::array<double, 4> weights{};
};

struct PmxMaterial {
  std::string name;
  FbxDouble3 diffuse;
  double alpha;
  int texture;
  int indexCount;
};

struct PmxBone {
  std::string name;
  FbxVector4 position;
  int parent;
};

struct PmxMorph {
  std::string name;
  std::vector<std::pair<int, FbxVector4>> deltas;
};

struct PmxModel {
  std::vector<PmxVertex> vertices;
  std::vector<int> indices;
  std::vector<std::string> textures;
  std::vector<PmxMaterial> materials;
  std::vector<PmxBone> bones;
  std::vector<PmxMorph> morphs;
  bool approximatedSkin = false;
  bool otherMorphs = false;
};

class PmxReader {
public:
  explicit PmxReader(std::istream& stream) : _stream(stream) {}

  bool Parse(PmxModel& model) {
	char magic[4]{};
	if (!ReadBytes(magic, sizeof(magic)) || std::memcmp(magic, "PMX ", sizeof(magic)) != 0)
	  return false;
	const float version = Read<float>();
	if (version != 2.0f && version != 2.1f)
	  return false;
	const auto count = Read<uint8_t>();
	if (count != _header.size() || !ReadBytes(_header.data(), _header.size()))
	  return false;
	if (_header[0] > 1 || _header[1] > 4)
	  return false;
	for (size_t i = 2; i < _header.size(); i++) {
	  const auto width = _header[i];
	  if (width != 1 && width != 2 && width != 4)
		return false;
	}
	for (int i = 0; i < 4; i++)
	  if (!SkipText())
		return false;
	return ReadVertices(model) && ReadIndices(model) && ReadTextures(model) && ReadMaterials(model) && ReadBones(model) && ReadMorphs(model);
  }

  const char* Section() const {
	return _section;
  }

private:
  std::istream& _stream;
  std::array<uint8_t, 8> _header{};
  const char* _section = "header";

  bool ReadBytes(void* data, size_t length) {
	return bool(_stream.read(static_cast<char*>(data), static_cast<std::streamsize>(length)));
  }

  template<class T>
  T Read() {
	T value{};
	ReadBytes(&value, sizeof(value));
	return value;
  }

  bool Skip(size_t length) {
	return bool(_stream.ignore(static_cast<std::streamsize>(length))) && _stream.gcount() == static_cast<std::streamsize>(length);
  }

  int Count(int max) {
	const auto count = Read<int32_t>();
	if (!_stream || count < 0 || count > max)
	  return -1;
	return count;
  }

  bool SkipText() {
	const int length = Count(1 << 24);
	return length >= 0 && Skip(length);
  }

  bool ReadText(std::string& output) {
	const int length = Count(1 << 24);
	if (length < 0)
	  return false;
	if (length == 0) {
	  output.clear();
	  return true;
	}
	if (_header[0] == 1) {
	  output.resize(length);
	  if (!ReadBytes(output.data(), length) || output.find(char(0)) != std::string::npos)
		return false;
	  return MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, output.data(), length, nullptr, 0) > 0;
	}
	if ((length & 1) != 0)
	  return false;
	std::wstring source(length / 2, L'\0');
	if (!ReadBytes(source.data(), length) || source.find(wchar_t(0)) != std::wstring::npos)
	  return false;
	const int size = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, source.data(), static_cast<int>(source.size()), nullptr, 0, nullptr, nullptr);
	if (size <= 0)
	  return false;
	output.resize(size);
	return WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, source.data(), static_cast<int>(source.size()), output.data(), size, nullptr, nullptr) == size;
  }

  bool ReadName(std::string& output) {
	std::string local;
	std::string english;
	if (!ReadText(local) || !ReadText(english))
	  return false;
	output = !local.empty() ? std::move(local) : std::move(english);
	return output.find_first_of("\n\r\t") == std::string::npos;
  }

  int Index(int kind, bool vertex = false) {
	switch (_header[kind]) {
	  case 1: return vertex ? int(Read<uint8_t>()) : int(Read<int8_t>());
	  case 2: return vertex ? int(Read<uint16_t>()) : int(Read<int16_t>());
	  case 4: return Read<int32_t>();
	  default: return -1;
	}
  }

  FbxVector4 Vector() {
	const float x = Read<float>();
	const float y = Read<float>();
	const float z = Read<float>();
	// Convert left-handed Y-up PMX to the same right-handed Z-up FBX axes as the UE donor.
	return FbxVector4(z, -x, y, 0.0);
  }

  bool ReadVertices(PmxModel& model) {
	_section = "vertices";
	const int count = Count(2000000);
	if (count <= 0)
	  return false;
	model.vertices.resize(count);
	for (auto& vertex : model.vertices) {
	  vertex.position = Vector();
	  vertex.normal = Vector();
	  const float u = Read<float>();
	  const float v = Read<float>();
	  vertex.uv = FbxVector2(u, 1.0 - v);
	  for (int i = 0; i < 3; i++)
		if (!std::isfinite(vertex.position[i]) || !std::isfinite(vertex.normal[i]))
		  return false;
	  if (!std::isfinite(u) || !std::isfinite(v) || !Skip(16 * _header[1]))
		return false;
	  const uint8_t skin = Read<uint8_t>();
	  switch (skin) {
		case 0:
		  vertex.bones[0] = Index(5);
		  vertex.weights[0] = 1.0;
		  break;
		case 1:
		case 3:
		  vertex.bones[0] = Index(5);
		  vertex.bones[1] = Index(5);
		  vertex.weights[0] = Read<float>();
		  vertex.weights[1] = 1.0 - vertex.weights[0];
		  if (skin == 3) {
			model.approximatedSkin = true;
			if (!Skip(36))
			  return false;
		  }
		  break;
		case 2:
		case 4:
		  if (skin == 4)
			model.approximatedSkin = true;
		  for (auto& bone : vertex.bones)
			bone = Index(5);
		  for (auto& weight : vertex.weights)
			weight = Read<float>();
		  break;
		default:
		  return false;
	  }
	  if (!Skip(4))
		return false;
	  double total = 0.0;
	  for (size_t i = 0; i < vertex.weights.size(); i++) {
		const double weight = vertex.weights[i];
		if (!std::isfinite(weight) || weight < 0.0 || weight > 1.0 || (weight > 0.0 && vertex.bones[i] < 0))
		  return false;
		total += weight;
	  }
	  if (total <= 0.0)
		return false;
	  for (auto& weight : vertex.weights)
		weight /= total;
	}
	return true;
  }

  bool ReadIndices(PmxModel& model) {
	_section = "triangle indices";
	const int count = Count(12000000);
	if (count <= 0 || count % 3 != 0)
	  return false;
	model.indices.resize(count);
	for (auto& index : model.indices) {
	  index = Index(2, true);
	  if (index < 0 || index >= static_cast<int>(model.vertices.size()))
		return false;
	}
	return !!_stream;
  }

  bool ReadTextures(PmxModel& model) {
	_section = "texture names";
	const int count = Count(65536);
	if (count < 0)
	  return false;
	model.textures.resize(count);
	for (auto& link : model.textures)
	  if (!ReadText(link))
		return false;
	return true;
  }

  bool ReadMaterials(PmxModel& model) {
	_section = "material slots";
	const int count = Count(65536);
	if (count <= 0)
	  return false;
	model.materials.resize(count);
	std::set<std::string> names;
	int remaining = static_cast<int>(model.indices.size());
	for (auto& material : model.materials) {
	  if (!ReadName(material.name))
		return false;
	  if (material.name.empty())
		material.name = "Material";
	  const std::string base = material.name;
	  for (int suffix = 2; !names.insert(material.name).second; suffix++)
		material.name = base + "_" + std::to_string(suffix);
	  const auto diffuse = Vector();
	  material.diffuse = FbxDouble3(diffuse[0], diffuse[1], diffuse[2]);
	  material.alpha = Read<float>();
	  if (!std::isfinite(material.alpha) || !std::isfinite(diffuse[0]) || !std::isfinite(diffuse[1]) || !std::isfinite(diffuse[2]))
		return false;
	  if (!Skip(3 * 4 + 4 + 3 * 4 + 1 + 4 * 4 + 4))
		return false;
	  material.texture = Index(3);
	  if (material.texture < -1 || material.texture >= static_cast<int>(model.textures.size()))
		return false;
	  Index(3);
	  Read<uint8_t>();
	  const auto toon = Read<uint8_t>();
	  if (toon == 0)
		Index(3);
	  else if (toon == 1)
		Read<uint8_t>();
	  else
		return false;
	  if (!SkipText())
		return false;
	  material.indexCount = Count(remaining);
	  if (material.indexCount < 0 || material.indexCount % 3 != 0)
		return false;
	  remaining -= material.indexCount;
	}
	return remaining == 0;
  }

  bool ReadBones(PmxModel& model) {
	_section = "bones and skin links";
	const int count = Count(65536);
	if (count <= 0)
	  return false;
	model.bones.resize(count);
	std::set<std::string> names;
	for (auto& bone : model.bones) {
	  if (!ReadName(bone.name) || bone.name.empty() || !names.insert(bone.name).second)
		return false;
	  bone.position = Vector();
	  for (int i = 0; i < 3; i++)
		if (!std::isfinite(bone.position[i]))
		  return false;
	  bone.parent = Index(5);
	  if (bone.parent < -1 || bone.parent >= count)
		return false;
	  Read<int32_t>();
	  const auto flags = Read<uint16_t>();
	  if ((flags & 0x0001) != 0)
		Index(5);
	  else if (!Skip(12))
		return false;
	  if ((flags & 0x0300) != 0 && !Skip(_header[5] + 4))
		return false;
	  if ((flags & 0x0400) != 0 && !Skip(12))
		return false;
	  if ((flags & 0x0800) != 0 && !Skip(24))
		return false;
	  if ((flags & 0x2000) != 0 && !Skip(4))
		return false;
	  if ((flags & 0x0020) != 0 && !SkipIk())
		return false;
	}
	std::vector<uint8_t> state(count);
	for (int i = 0; i < count; i++) {
	  int node = i;
	  while (node != -1 && state[node] == 0) {
		state[node] = 1;
		node = model.bones[node].parent;
	  }
	  if (node != -1 && state[node] == 1)
		return false;
	  node = i;
	  while (node != -1 && state[node] == 1) {
		state[node] = 2;
		node = model.bones[node].parent;
	  }
	}
	for (const auto& vertex : model.vertices)
	  for (size_t i = 0; i < vertex.bones.size(); i++)
		if (vertex.weights[i] > 0.0 && vertex.bones[i] >= count)
		  return false;
	return true;
  }

  bool SkipIk() {
	if (!Skip(_header[5] + 8))
	  return false;
	const int links = Count(65536);
	if (links < 0)
	  return false;
	for (int i = 0; i < links; i++) {
	  Index(5);
	  const auto angle = Read<uint8_t>();
	  if (angle > 1 || (angle == 1 && !Skip(24)))
		return false;
	}
	return !!_stream;
  }

  bool ReadMorphs(PmxModel& model) {
	_section = "morphs";
	const int count = Count(65536);
	if (count < 0)
	  return false;
	for (int i = 0; i < count; i++) {
	  PmxMorph morph;
	  if (!ReadName(morph.name))
		return false;
	  if (morph.name.empty())
		morph.name = "Morph_" + std::to_string(i);
	  Read<uint8_t>();
	  const uint8_t kind = Read<uint8_t>();
	  const int offsets = Count(2000000);
	  if (offsets < 0)
		return false;
	  if (kind == 1) {
		if (offsets > 0 && (model.morphs.size() + 1) * model.vertices.size() > 10000000)
		  return false;
		morph.deltas.reserve(offsets);
		for (int j = 0; j < offsets; j++) {
		  const int vertex = Index(2, true);
		  const auto delta = Vector();
		  if (vertex < 0 || vertex >= static_cast<int>(model.vertices.size()))
			return false;
		  for (int axis = 0; axis < 3; axis++)
			if (!std::isfinite(delta[axis]))
			  return false;
		  morph.deltas.emplace_back(vertex, delta);
		}
		if (!morph.deltas.empty())
		  model.morphs.push_back(std::move(morph));
		continue;
	  }
	  size_t size = 0;
	  switch (kind) {
		case 0:
		case 9: size = _header[6] + 4; break;
		case 2: size = _header[5] + 28; break;
		case 3:
		case 4:
		case 5:
		case 6:
		case 7: size = _header[2] + 16; break;
		case 8: size = _header[4] + 113; break;
		case 10: size = _header[7] + 25; break;
		default: return false;
	  }
	  if (offsets > 0)
		model.otherMorphs = true;
	  if (!Skip(size * offsets))
		return false;
	}
	return !!_stream;
  }
};

static void AddMaterials(FbxScene* scene, FbxNode* node, const PmxModel& model, const std::filesystem::path& file) {
  for (const auto& source : model.materials) {
	auto* material = FbxSurfaceLambert::Create(scene, source.name.c_str());
	material->Diffuse.Set(source.diffuse);
	material->TransparencyFactor.Set(1.0 - source.alpha);
	if (source.texture >= 0 && !model.textures[source.texture].empty()) {
	  const auto& name = model.textures[source.texture];
	  auto absolute = (std::filesystem::absolute(file).parent_path() / std::filesystem::u8path(name)).lexically_normal().u8string();
	  auto* texture = FbxFileTexture::Create(scene, ("Diffuse_" + source.name).c_str());
	  texture->SetFileName(absolute.c_str());
	  texture->SetRelativeFileName(name.c_str());
	  texture->SetMaterialUse(FbxFileTexture::eModelMaterial);
	  texture->SetTextureUse(FbxTexture::eStandard);
	  texture->SetMappingType(FbxTexture::eUV);
	  material->Diffuse.ConnectSrcObject(texture);
	}
	node->AddMaterial(material);
  }
}

static void AddMesh(FbxMesh* mesh, const PmxModel& model) {
  mesh->InitControlPoints(static_cast<int>(model.vertices.size()));
  auto* normals = mesh->CreateElementNormal();
  normals->SetMappingMode(FbxLayerElement::eByControlPoint);
  normals->SetReferenceMode(FbxLayerElement::eDirect);
  auto* uv = mesh->CreateElementUV("UV0");
  uv->SetMappingMode(FbxLayerElement::eByControlPoint);
  uv->SetReferenceMode(FbxLayerElement::eDirect);
  auto* materials = mesh->CreateElementMaterial();
  materials->SetMappingMode(FbxLayerElement::eByPolygon);
  materials->SetReferenceMode(FbxLayerElement::eIndexToDirect);
  for (size_t i = 0; i < model.vertices.size(); i++) {
	const auto& vertex = model.vertices[i];
	mesh->SetControlPointAt(vertex.position, static_cast<int>(i));
	normals->GetDirectArray().Add(vertex.normal);
	uv->GetDirectArray().Add(vertex.uv);
  }
  size_t index = 0;
  for (size_t material = 0; material < model.materials.size(); material++) {
	for (int j = 0; j < model.materials[material].indexCount; j += 3) {
	  mesh->BeginPolygon(static_cast<int>(material));
	  mesh->AddPolygon(model.indices[index]);
	  mesh->AddPolygon(model.indices[index + 2]);
	  mesh->AddPolygon(model.indices[index + 1]);
	  index += 3;
	  mesh->EndPolygon();
	}
  }
}

static std::vector<FbxNode*> AddBones(FbxScene* scene, const PmxModel& model) {
  std::vector<FbxNode*> nodes;
  nodes.reserve(model.bones.size());
  for (const auto& bone : model.bones) {
	auto* node = FbxNode::Create(scene, bone.name.c_str());
	auto* skeleton = FbxSkeleton::Create(scene, bone.name.c_str());
	skeleton->SetSkeletonType(bone.parent == -1 ? FbxSkeleton::eRoot : FbxSkeleton::eLimbNode);
	node->SetNodeAttribute(skeleton);
	const auto local = bone.parent == -1 ? bone.position : bone.position - model.bones[bone.parent].position;
	node->LclTranslation.Set(FbxDouble3(local[0], local[1], local[2]));
	nodes.push_back(node);
  }
  for (size_t i = 0; i < model.bones.size(); i++) {
	const int parent = model.bones[i].parent;
	if (parent == -1)
	  scene->GetRootNode()->AddChild(nodes[i]);
	else
	  nodes[parent]->AddChild(nodes[i]);
  }
  return nodes;
}

static void AddSkin(FbxScene* scene, FbxMesh* mesh, FbxNode* meshNode, const PmxModel& model, const std::vector<FbxNode*>& bones) {
  auto* skin = FbxSkin::Create(scene, "PMX Skin");
  std::vector<FbxCluster*> clusters(bones.size());
  const auto meshBind = meshNode->EvaluateGlobalTransform();
  for (size_t vertex = 0; vertex < model.vertices.size(); vertex++) {
	const auto& source = model.vertices[vertex];
	for (size_t i = 0; i < source.bones.size(); i++) {
	  if (source.weights[i] == 0.0)
		continue;
	  const auto bone = source.bones[i];
	  if (!clusters[bone]) {
		auto* cluster = FbxCluster::Create(scene, (model.bones[bone].name + "_Skin").c_str());
		cluster->SetLink(bones[bone]);
		cluster->SetLinkMode(FbxCluster::eNormalize);
		cluster->SetTransformMatrix(meshBind);
		cluster->SetTransformLinkMatrix(bones[bone]->EvaluateGlobalTransform());
		skin->AddCluster(cluster);
		clusters[bone] = cluster;
	  }
	  clusters[bone]->AddControlPointIndex(static_cast<int>(vertex), source.weights[i]);
	}
  }
  mesh->AddDeformer(skin);
}

static void AddMorphs(FbxScene* scene, FbxMesh* mesh, const PmxModel& model) {
  if (model.morphs.empty())
	return;
  auto* blend = FbxBlendShape::Create(scene, "PMX Vertex Morphs");
  std::set<std::string> names;
  for (const auto& morph : model.morphs) {
	std::string name = morph.name;
	for (int suffix = 2; !names.insert(name).second; suffix++)
	  name = morph.name + "_" + std::to_string(suffix);
	auto* channel = FbxBlendShapeChannel::Create(scene, name.c_str());
	auto* shape = FbxShape::Create(scene, name.c_str());
	shape->InitControlPoints(static_cast<int>(model.vertices.size()));
	auto* points = shape->GetControlPoints();
	for (size_t vertex = 0; vertex < model.vertices.size(); vertex++)
	  points[vertex] = model.vertices[vertex].position;
	for (const auto& [vertex, delta] : morph.deltas)
	  points[vertex] += delta;
	channel->AddTargetShape(shape);
	blend->AddBlendShapeChannel(channel);
  }
  mesh->AddDeformer(blend);
}

FbxScene* LoadPmx(FbxManager* manager, const std::filesystem::path& file, std::string& error, bool reportLimits) {
  std::ifstream stream(file, std::ios::binary);
  if (!stream) {
	error = "Could not open PMX file.";
	return nullptr;
  }
  PmxModel model;
  PmxReader reader(stream);
  if (!reader.Parse(model)) {
	error = std::string("Unsupported or invalid PMX 2.x data: ") + reader.Section();
	return nullptr;
  }
  auto* scene = FbxScene::Create(manager, "PMX Source");
  const FbxAxisSystem axis(FbxAxisSystem::eZAxis, FbxAxisSystem::eParityOdd, FbxAxisSystem::eRightHanded);
  scene->GetGlobalSettings().SetAxisSystem(axis);
  scene->GetGlobalSettings().SetSystemUnit(FbxSystemUnit(8.0));
  auto* node = FbxNode::Create(scene, "PMX_Model");
  auto* mesh = FbxMesh::Create(scene, "PMX_Mesh");
  node->SetNodeAttribute(mesh);
  scene->GetRootNode()->AddChild(node);
  AddMaterials(scene, node, model, file);
  AddMesh(mesh, model);
  const auto bones = AddBones(scene, model);
  AddSkin(scene, mesh, node, model, bones);
  AddMorphs(scene, mesh, model);
  if (reportLimits && model.approximatedSkin)
	std::fprintf(stderr, "PMX: SDEF/QDEF skinning converted to normalized linear skinning.\n");
  if (reportLimits && model.otherMorphs)
	std::fprintf(stderr, "PMX: non-vertex morphs are omitted; only vertex morphs are exported.\n");
  return scene;
}
