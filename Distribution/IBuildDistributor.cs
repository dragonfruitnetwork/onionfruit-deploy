using System.Threading.Tasks;

namespace DragonFruit.OnionFruit.Deploy.Distribution;

public interface IBuildDistributor
{
    Task RestoreBuild();
    Task PublishBuild(string version);
}