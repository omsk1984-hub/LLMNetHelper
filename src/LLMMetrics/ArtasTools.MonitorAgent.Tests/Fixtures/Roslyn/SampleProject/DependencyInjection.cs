namespace SampleProject;

/// <summary>
/// Фиктивная регистрация сервисов — для проверки StructureAnalyzer.
/// </summary>
public static class SampleDependencyInjection
{
    public static void AddSampleServices(IServiceCollection services)
    {
        services.AddSingleton<IOrderRepository, InMemoryOrderRepository>();
    }
}

/// <summary>
/// Заглушка контейнера, чтобы фикстура компилировалась без Microsoft.Extensions.DependencyInjection.
/// </summary>
public interface IServiceCollection
{
    void AddSingleton<TService, TImplementation>()
        where TImplementation : TService;
}

/// <summary>
/// Реализация репозитория в памяти.
/// </summary>
public sealed class InMemoryOrderRepository : IOrderRepository
{
    public Task<Order> GetAsync(int orderId) => Task.FromResult(new Order { Id = orderId });

    public void Save(int orderId)
    {
        // Ничего не делает — заглушка.
    }
}
