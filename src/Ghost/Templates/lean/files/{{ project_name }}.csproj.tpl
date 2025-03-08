<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AssemblyName>{{ safe_name }}</AssemblyName>
    <RootNamespace>{{ defaultNamespace }}</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Ghost.SDK" Version="1.0.0" />
  </ItemGroup>
</Project>