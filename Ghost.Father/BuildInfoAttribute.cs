using System;

namespace Ghost.Father
{
  [AttributeUsage(AttributeTargets.Assembly)]
  public class BuildInfoAttribute : Attribute
  {
    public int BuildNumber { get; }

    public BuildInfoAttribute(int buildNumber)
    {
      BuildNumber = buildNumber;
    }
  }
}
