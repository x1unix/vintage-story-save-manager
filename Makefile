.PHONY: build test publish

build:
	dotnet build VintageStorySaveManager.slnx

test:
	dotnet test VintageStorySaveManager.slnx

publish:
	dotnet publish src/VintageStorySaveManager/VintageStorySaveManager.csproj -c Release -o dist
