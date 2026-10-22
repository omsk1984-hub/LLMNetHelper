namespace SampleProject;

/// <summary>
/// Тестовый класс для анализа: содержит намеренно проблемные места.
/// </summary>
public sealed class OrderProcessor
{
    private readonly IOrderRepository _repository;

    public OrderProcessor(IOrderRepository repository)
    {
        _repository = repository;
    }

    /// <summary>
    /// Обрабатывает заказ.
    /// </summary>
    public async Task<int> ProcessOrderAsync(int orderId)
    {
        try
        {
            var order = await _repository.GetAsync(orderId);
            return order.Total;
        }
        catch (Exception)
        {
            // Проблема: catch(Exception) без логирования — FailurePointAnalyzer должен найти.
            return 0;
        }
    }

    /// <summary>
    /// Метод с пустым catch.
    /// </summary>
    public void SaveOrder(int orderId)
    {
        try
        {
            _repository.Save(orderId);
        }
        catch
        {
            // Проблема: пустой catch — тоже должна обнаруживаться.
        }
    }

    /// <summary>
    /// Метод, вызывающий ProcessOrderAsync — для проверки графа вызовов.
    /// </summary>
    public async Task RunAsync()
    {
        await ProcessOrderAsync(1);
        SaveOrder(2);
    }

    /// <summary>
    /// Метод с throw null.
    /// </summary>
    public void Fail(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            throw null!;
        }
    }
}

/// <summary>
/// Абстракция репозитория — нужна для компиляции фикстуры.
/// </summary>
public interface IOrderRepository
{
    Task<Order> GetAsync(int orderId);

    void Save(int orderId);
}

/// <summary>
/// Простая модель заказа.
/// </summary>
public sealed class Order
{
    public int Id { get; set; }

    public int Total { get; set; }
}
