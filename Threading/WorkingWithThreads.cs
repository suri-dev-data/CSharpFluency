using System;
using System.Collections.Generic;
using System.Threading;

class SurianoWorkingWithThreads
{
    // Contadores atômicos
    private static int totalTasksProduced = 0;
    private static int totalTasksProcessed = 0;

    // Fila de trabalho protegida por Monitor (lock)
    private static readonly Queue<string> workQueue = new Queue<string>();
    private static readonly object queueLock = new object();

    // Semaphore para limitar conexões "simuladas" ao DB (máx 3)
    private static readonly Semaphore dbSemaphore = new Semaphore(3, 3);

    // Mutex nomeado (simulação de recurso entre processos)
    private static readonly Mutex globalMutex = new Mutex(false, "Global\\ComplexThreadDemoMutex");

    // Timer para relatório periódico
    private static Timer statsTimer;

    // Flag para parada
    private static CancellationTokenSource cts = new CancellationTokenSource();

    static void Main()
    {
        Console.WriteLine("=== ComplexThreadDemo START ===");

        // Capturando ExecutionContext (mostra que contexto lógico pode ser preservado)
        var ec = ExecutionContext.Capture();

        //thread que produz trabalho usando ParameterizedThreadStart
        Thread producer = new Thread(new ParameterizedThreadStart(Producer));
        producer.Name = "ProducerThread";
        producer.Priority = ThreadPriority.AboveNormal;
        producer.IsBackground = false;
        producer.Start(ec); // passo ExecutionContext só pra demonstrar

        //  thread consumer dedicada usando ThreadStart
        Thread consumer = new Thread(new ThreadStart(ConsumerLoop));
        consumer.Name = "DedicatedConsumer";
        consumer.Priority = ThreadPriority.Normal;
        consumer.IsBackground = false;
        consumer.Start();

        // Iniciando alguns workers no ThreadPool
        for (int i = 0; i < 5; i++)
        {
            int workerId = i; // copia local para o closure
            ThreadPool.QueueUserWorkItem(state => ThreadPoolWorker((int)state), workerId);
        }

        // Timer que printa estatísticas a cada 2s
        statsTimer = new Timer(StatsTimerCallback, null, dueTime: 2000, period: 2000);

        //   Demonstrar Mutex: tentamos adquirir/usar recurso global por 3s
        // rodando em outra thread para não bloquear o main
        Thread crossProcSimulator = new Thread(() =>
        {
            try
            {
                Console.WriteLine("[MutexSim] Tentando adquirir mutex global...");
                if (globalMutex.WaitOne(5000))
                {
                    Console.WriteLine("[MutexSim] Mutex adquirido - fazendo trabalho crítico por 2s...");
                    Thread.Sleep(2000);
                    globalMutex.ReleaseMutex();
                    Console.WriteLine("[MutexSim] Mutex liberado.");
                }
                else
                {
                    Console.WriteLine("[MutexSim] Não conseguiu mutex (timeout).");
                }
            }
            catch (AbandonedMutexException)
            {
                Console.WriteLine("[MutexSim] Mutex abandonado detectado.");
            }
        });
        crossProcSimulator.Name = "MutexSimulator";
        crossProcSimulator.Start();

        // Demonstração de interrupção: vamos lançar Interrupt no consumer após 10s
        Thread interrupter = new Thread(() =>
        {
            Thread.Sleep(10000);
            Console.WriteLine("[Interrupter] Chamando Interrupt() na DedicatedConsumer...");
            consumer.Interrupt(); // acorda a thread caso esteja sleeping/waiting
        });
        interrupter.IsBackground = true;
        interrupter.Start();

        //  Espera por um tempo e depois ordena parada graciosa
        Thread.Sleep(15000);
        Console.WriteLine("Main pedindo parada graciosa...");
        cts.Cancel();

        // Acorda o consumer caso esteja esperando pela fila
        lock (queueLock)
        {
            Monitor.PulseAll(queueLock);
        }

        // Espera as threads terminarem de forma ordenada
        Console.WriteLine("Main aguardando producer, consumer e simulator...");
        producer.Join();
        consumer.Join();
        crossProcSimulator.Join();

        // Dispose timer e semaphore
        statsTimer.Dispose();
        dbSemaphore.Dispose();
        globalMutex.Dispose();

        Console.WriteLine("Total produzidos: " + Interlocked.CompareExchange(ref totalTasksProduced, 0, 0));
        Console.WriteLine("Total processados: " + Interlocked.CompareExchange(ref totalTasksProcessed, 0, 0));
        Console.WriteLine("=== ComplexThreadDemo END ===");
    }

    // Producer que enfileira mensagens periodicamente.
    // Recebe ExecutionContext encapsulado via ParameterizedThreadStart para demonstrar uso.
    static void Producer(object state)
    {
        // 'state' vem do Start(ec). Vou restaurar o contexto só pra demonstrar:
        var ec = state as ExecutionContext;
        if (ec != null)
        {
            ExecutionContext.Run(ec, _ =>
            {
                // Dentro do ExecutionContext "restaurado"
                Console.WriteLine($"[{Thread.CurrentThread.Name}] Producer executando dentro de ExecutionContext.");
            }, null);
        }

        int id = 0;
        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                string job = $"Job-{++id}-from-{Thread.CurrentThread.ManagedThreadId}";

                // Protegendo a fila com lock (Monitor)
                lock (queueLock)
                {
                    workQueue.Enqueue(job);
                    Interlocked.Increment(ref totalTasksProduced);
                    // sinaliza consumidores
                    Monitor.Pulse(queueLock);
                }

                Console.WriteLine($"[{Thread.CurrentThread.Name}] Enfileirado: {job} (Produced={totalTasksProduced})");

                // Produz nova tarefa a cada 700..1200ms
                Thread.Sleep(700 + (id % 5) * 100);
            }
        }
        catch (ThreadInterruptedException)
        {
            Console.WriteLine($"[{Thread.CurrentThread.Name}] Producer interrompido.");
        }
        finally
        {
            Console.WriteLine($"[{Thread.CurrentThread.Name}] Producer terminando. IsAlive={Thread.CurrentThread.IsAlive}");
        }
    }

    // Consumer que consome da fila. Usa Join/Interrupt do main para demonstração.
    static void ConsumerLoop()
    {
        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                string job = null;

                lock (queueLock)
                {
                    // aguarda se fila vazia
                    while (workQueue.Count == 0)
                    {
                        // Se cancelarem, sair
                        if (cts.Token.IsCancellationRequested) break;

                        Console.WriteLine($"[{Thread.CurrentThread.Name}] Fila vazia -> aguardando (ThreadState={Thread.CurrentThread.ThreadState})");
                        Monitor.Wait(queueLock, 2000); // espera com timeout, pode ser interrompido por Interrupt()
                    }

                    if (workQueue.Count > 0)
                        job = workQueue.Dequeue();
                }

                if (job == null)
                {
                    // sem job desta iteração, checar cancelamento
                    continue;
                }

                Console.WriteLine($"[{Thread.CurrentThread.Name}] Pegou {job} para processar.");

                // Usando Semaphore para limitar "conexões" concorrentes (ex.: DB)
                dbSemaphore.WaitOne();
                try
                {
                    // Simula trabalho que pode ser interrompido
                    Console.WriteLine($"[{Thread.CurrentThread.Name}] Processando {job} (acquired DB slot) ...");
                    // Simula operação IO/CPU
                    for (int i = 0; i < 5; i++)
                    {
                        Thread.Sleep(200); // pode ser acordado por Interrupt
                    }

                    Interlocked.Increment(ref totalTasksProcessed);
                    Console.WriteLine($"[{Thread.CurrentThread.Name}] Finalizado {job} (Processed={totalTasksProcessed})");
                }
                finally
                {
                    dbSemaphore.Release();
                }
            }
        }
        catch (ThreadInterruptedException)
        {
            Console.WriteLine($"[{Thread.CurrentThread.Name}] Fui interrompido (Interrupt)!");
        }
        finally
        {
            Console.WriteLine($"[{Thread.CurrentThread.Name}] Saindo do loop de consumo.");
        }
    }

    // Worker do ThreadPool que processa uma "subtarefa" sem usar Threads manuais
    static void ThreadPoolWorker(int workerId)
    {
        // Demonstrando ThreadPool e ThreadPriority (não se controla prioridade do pool; apenas ilustrativo para thread atual)
        var t = Thread.CurrentThread;
        Console.WriteLine($"[ThreadPoolWorker-{workerId}] rodando em ThreadId={t.ManagedThreadId}, IsThreadPoolThread={t.IsThreadPoolThread}, Priority={t.Priority}");

        // Simulamos pegar um job da fila de forma não bloqueante
        string job = null;
        lock (queueLock)
        {
            if (workQueue.Count > 0)
                job = workQueue.Dequeue();
        }

        if (job != null)
        {
            // Tenta usar Mutex global por segurança antes de acessar recurso "compartilhado entre processos"
            bool got = false;
            try
            {
                got = globalMutex.WaitOne(0); // tentativa não bloqueante
                if (got)
                {
                    Console.WriteLine($"[ThreadPoolWorker-{workerId}] conseguiu Mutex, processando {job}");
                    Thread.Sleep(300); // operação crítica
                }
                else
                {
                    Console.WriteLine($"[ThreadPoolWorker-{workerId}] não conseguiu Mutex, processando sem exclusão {job}");
                    Thread.Sleep(150);
                }

                Interlocked.Increment(ref totalTasksProcessed);
            }
            finally
            {
                if (got)
                    globalMutex.ReleaseMutex();
            }
        }
        else
        {
            Console.WriteLine($"[ThreadPoolWorker-{workerId}] sem job, saindo.");
        }
    }

    // Timer callback: apenas imprime estatísticas rápidas
    static void StatsTimerCallback(object state)
    {
        int prod = Interlocked.CompareExchange(ref totalTasksProduced, 0, 0);
        int proc = Interlocked.CompareExchange(ref totalTasksProcessed, 0, 0);
        Console.WriteLine($"[StatsTimer] Produced={prod}, Processed={proc}, QueueSize={GetQueueSizeSafe()}");
    }

    // Método para obter tamanho da fila de forma segura
    static int GetQueueSizeSafe()
    {
        lock (queueLock)
        {
            return workQueue.Count;
        }
    }
}
