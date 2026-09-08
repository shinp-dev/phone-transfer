using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PhoneTransfer.Infrastructure.Discovery;

[SupportedOSPlatform("windows")]
public sealed class WindowsMdnsAdvertiser : IAsyncDisposable
{
    private const uint DnsRequestPending = 9506;
    private static readonly RegisterComplete CompletionCallback = Completion;
    private readonly object gate = new();
    private readonly TaskCompletionSource<uint> registration = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IntPtr serviceInstance;
    private IntPtr requestMemory;
    private GCHandle selfHandle;
    private bool registrationCompleted;
    private bool disposeRequested;
    private bool deregisterStarted;
    private bool cleaned;

    private WindowsMdnsAdvertiser(MdnsAdvertisement advertisement)
    {
        var bytes = advertisement.Address.GetAddressBytes();
        var ipv4 = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        serviceInstance = DnsServiceConstructInstance(
            advertisement.InstanceName,
            advertisement.HostName,
            ref ipv4,
            IntPtr.Zero,
            advertisement.Port,
            0,
            0,
            checked((uint)advertisement.TxtKeys.Length),
            advertisement.TxtKeys,
            advertisement.TxtValues);
        if (serviceInstance == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "mDNS service instance could not be created.");
        }

        selfHandle = GCHandle.Alloc(this);
        var request = new DnsServiceRegisterRequest
        {
            Version = 1,
            InterfaceIndex = advertisement.InterfaceIndex,
            ServiceInstance = serviceInstance,
            RegisterCompletionCallback = Marshal.GetFunctionPointerForDelegate(CompletionCallback),
            QueryContext = GCHandle.ToIntPtr(selfHandle),
            Credentials = IntPtr.Zero,
            UnicastEnabled = 0
        };
        requestMemory = Marshal.AllocHGlobal(Marshal.SizeOf<DnsServiceRegisterRequest>());
        Marshal.StructureToPtr(request, requestMemory, false);
        var status = DnsServiceRegister(requestMemory, IntPtr.Zero);
        if (status != DnsRequestPending)
        {
            ReleaseNative();
            throw new Win32Exception(checked((int)status), "mDNS service registration could not be started.");
        }
    }

    public Task<uint> Registration => registration.Task;

    public static WindowsMdnsAdvertiser Start(MdnsAdvertisement advertisement) => new(advertisement);

    public async ValueTask DisposeAsync()
    {
        var beginDeregister = false;
        lock (gate)
        {
            if (cleaned) return;
            disposeRequested = true;
            if (registrationCompleted && !deregisterStarted)
            {
                deregisterStarted = true;
                beginDeregister = true;
            }
        }
        if (beginDeregister) BeginDeregister();
        try
        {
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The static callback and GCHandle keep native state valid until Windows completes deregistration.
        }
    }

    private static void Completion(uint status, IntPtr queryContext, IntPtr instanceCopy)
    {
        try
        {
            if (queryContext == IntPtr.Zero) return;
            var handle = GCHandle.FromIntPtr(queryContext);
            if (handle.Target is WindowsMdnsAdvertiser advertiser) advertiser.OnCompletion(status);
        }
        finally
        {
            if (instanceCopy != IntPtr.Zero) DnsServiceFreeInstance(instanceCopy);
        }
    }

    private void OnCompletion(uint status)
    {
        var beginDeregister = false;
        var release = false;
        lock (gate)
        {
            if (cleaned) return;
            if (deregisterStarted)
            {
                release = true;
            }
            else
            {
                registrationCompleted = true;
                registration.TrySetResult(status);
                if (status != 0)
                {
                    release = true;
                }
                else if (disposeRequested)
                {
                    deregisterStarted = true;
                    beginDeregister = true;
                }
            }
        }
        if (beginDeregister) BeginDeregister();
        if (release) ReleaseNative();
    }

    private void BeginDeregister()
    {
        var status = DnsServiceDeRegister(requestMemory, IntPtr.Zero);
        if (status != DnsRequestPending) ReleaseNative();
    }

    private void ReleaseNative()
    {
        IntPtr request;
        IntPtr instance;
        GCHandle handle;
        lock (gate)
        {
            if (cleaned) return;
            cleaned = true;
            request = requestMemory;
            requestMemory = IntPtr.Zero;
            instance = serviceInstance;
            serviceInstance = IntPtr.Zero;
            handle = selfHandle;
            selfHandle = default;
        }
        if (request != IntPtr.Zero) Marshal.FreeHGlobal(request);
        if (instance != IntPtr.Zero) DnsServiceFreeInstance(instance);
        if (handle.IsAllocated) handle.Free();
        finished.TrySetResult(true);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void RegisterComplete(uint status, IntPtr queryContext, IntPtr serviceInstance);

    [StructLayout(LayoutKind.Sequential)]
    private struct DnsServiceRegisterRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr ServiceInstance;
        public IntPtr RegisterCompletionCallback;
        public IntPtr QueryContext;
        public IntPtr Credentials;
        public int UnicastEnabled;
    }

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr DnsServiceConstructInstance(
        string serviceName,
        string hostName,
        ref uint ipv4,
        IntPtr ipv6,
        ushort port,
        ushort priority,
        ushort weight,
        uint propertyCount,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 7)] string[] keys,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr, SizeParamIndex = 7)] string[] values);

    [DllImport("dnsapi.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint DnsServiceRegister(IntPtr request, IntPtr cancel);

    [DllImport("dnsapi.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint DnsServiceDeRegister(IntPtr request, IntPtr cancel);

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    private static extern void DnsServiceFreeInstance(IntPtr serviceInstance);
}
