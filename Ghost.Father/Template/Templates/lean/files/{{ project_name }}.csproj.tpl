<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net9.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AssemblyName>{{ defaultNamespace }}</AssemblyName>
    <RootNamespace>{{ defaultNamespace }}</RootNamespace>
    <GhostInstallDir>{{ ghost_install_dir }}</GhostInstallDir>
  </PropertyGroup>

  <ItemGroup>
    <Reference Include="Ghost.SDK">
      <HintPath>$(GhostInstallDir)\libs\Ghost.SDK.dll</HintPath>
    </Reference>
    <Reference Include="Ghost.Core">
      <HintPath>$(GhostInstallDir)\libs\Ghost.Core.dll</HintPath>
    </Reference>
  </ItemGroup>
</Project>