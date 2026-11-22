#pragma once

#include <fbxsdk.h>
#include <filesystem>
#include <string>

FbxScene* LoadPmx(FbxManager* manager, const std::filesystem::path& file, std::string& error, bool reportLimits = true);
