using System;
using System.Threading;
using System.Threading.Tasks;

class Program
{

    static int sharedCounter = 0;
    static SemaphoreSlim semaphore = new SemaphoreSlim(2, 2);
    static Mutex mutex = new Mutex();
    static Timer timer;

    static async Task Main()
    {
        Console.WriteLine("=== Task-based Multithreading Madness Start ===");

        // Timer with TimerCallback (still works fine with Tasks)
        timer = new Timer(state =>
        {
            Console.WriteLine($"[TIMER] Tick at {DateTime.Now:T} | Thread: {Thread.CurrentThread.ManagedThreadId}");
        }, null, 0, 1000);

        //simple loop with Interlocked
        Task t1 = Task.Run(() =>
        {
            Console.WriteLine($"[Task] Hello from Task 1 (Thread {Thread.CurrentThread.ManagedThreadId})");
            for (int i = 0; i < 5; i++)
            {
                int newValue = Interlocked.Increment(ref sharedCounter);
                Console.WriteLine($"Task 1 incremented counter to {newValue}");
                Thread.Sleep(200); // just to simulate some work
            }
        });

        //parameterized work ( Apenas para as tasks do tipo tasks 2 nao se colidirem uma com a outra )
        Task t2 = Task.Run(() =>
        {
            string msg = "Testing async method";
            lock (typeof(Program))
            {
                Console.WriteLine($"[Task] Task 2 (Thread {Thread.CurrentThread.ManagedThreadId}) says: {msg}");
                Thread.Sleep(500);
            }
        });

        //using Mutex (old school exclusive lock)
        Task t3 = Task.Run(() =>
        {
            Console.WriteLine($"Task 3 waiting for mutex...");
            mutex.WaitOne();
            Console.WriteLine($"Task 3 got the mutex!");
            Thread.Sleep(500);
            mutex.ReleaseMutex();
            Console.WriteLine($"Task 3 released the mutex!");
        });

        // Multiple tasks using SemaphoreSlim
        Task[] labTasks = new Task[5];
        for (int i = 0; i < 5; i++)
        {
            labTasks[i] = Task.Run(async () =>
            {
                Console.WriteLine($"Task {Task.CurrentId} waiting to enter the lab...");
                await semaphore.WaitAsync();
                Console.WriteLine($"Task {Task.CurrentId} entered the lab!");
                await Task.Delay(1000);
                Console.WriteLine($"Task {Task.CurrentId} leaving the lab...");
                semaphore.Release();
            });
        }

        //run something on thread pool automatically
        Task t4 = Task.Run(async () =>
        {
            Console.WriteLine($"[TaskPool] Task 4 running on Thread {Thread.CurrentThread.ManagedThreadId}");
            await Task.Delay(700);
        });

        // Wait for all tasks to finish (like Join, but async style)
        await Task.WhenAll(t1, t2, t3, t4, Task.WhenAll(labTasks));

        Console.WriteLine("=== All tasks finished ===");
        Console.WriteLine($"Final counter: {sharedCounter}");
    }
}
