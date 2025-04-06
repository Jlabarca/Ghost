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

    <!-- Additional dependencies for the full template -->
    <PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="9.0.0" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="9.0.0" />
  </ItemGroup>

  <ItemGroup>
    <Content Include="appsettings.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
    <Content Include="appsettings.*.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
      <DependentUpon>appsettings.json</DependentUpon>
    </Content>
  </ItemGroup>
</Project>