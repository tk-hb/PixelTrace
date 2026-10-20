#include "core/platform/device_discovery.h"
#include "core/common/logging/logging.h"
#define VK_NO_PROTOTYPES
#include <vulkan/vulkan.h>
#include <dlfcn.h>
#include <vector>

namespace {
struct VulkanDiscovery {
    void * library = nullptr;
    VkInstance instance = VK_NULL_HANDLE;
    PFN_vkDestroyInstance destroy = nullptr;

    ~VulkanDiscovery() {
        if (instance && destroy) destroy(instance, nullptr);
        if (library) dlclose(library);
    }
};
}

namespace onnxruntime {
std::unordered_set<OrtHardwareDevice> DeviceDiscovery::DiscoverDevicesForPlatform() {
    std::unordered_set<OrtHardwareDevice> devices;
    devices.emplace(GetCpuDeviceFromCPUIDInfo());

    VulkanDiscovery vk;
    vk.library = dlopen("libvulkan.so", RTLD_NOW | RTLD_LOCAL);
    if (!vk.library) {
        LOGS_DEFAULT(WARNING) << "Android Vulkan loader unavailable: " << dlerror();
        return devices;
    }
    auto proc = reinterpret_cast<PFN_vkGetInstanceProcAddr>(dlsym(vk.library, "vkGetInstanceProcAddr"));
    if (!proc) return devices;
    auto create = reinterpret_cast<PFN_vkCreateInstance>(proc(VK_NULL_HANDLE, "vkCreateInstance"));
    if (!create) return devices;
    VkApplicationInfo application{};
    application.sType = VK_STRUCTURE_TYPE_APPLICATION_INFO;
    application.pApplicationName = "PixelTrace";
    application.apiVersion = VK_API_VERSION_1_0;
    VkInstanceCreateInfo info{};
    info.sType = VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO;
    info.pApplicationInfo = &application;
    VkResult status = create(&info, nullptr, &vk.instance);
    if (status != VK_SUCCESS) {
        LOGS_DEFAULT(WARNING) << "Android Vulkan instance discovery failed: " << status;
        return devices;
    }
    vk.destroy = reinterpret_cast<PFN_vkDestroyInstance>(proc(vk.instance, "vkDestroyInstance"));
    auto enumerate = reinterpret_cast<PFN_vkEnumeratePhysicalDevices>(proc(vk.instance, "vkEnumeratePhysicalDevices"));
    auto properties = reinterpret_cast<PFN_vkGetPhysicalDeviceProperties>(proc(vk.instance, "vkGetPhysicalDeviceProperties"));
    auto queues = reinterpret_cast<PFN_vkGetPhysicalDeviceQueueFamilyProperties>(proc(vk.instance, "vkGetPhysicalDeviceQueueFamilyProperties"));
    if (!vk.destroy || !enumerate || !properties || !queues) return devices;
    uint32_t count = 0;
    if (enumerate(vk.instance, &count, nullptr) != VK_SUCCESS || count == 0) return devices;
    std::vector<VkPhysicalDevice> physical(count);
    status = enumerate(vk.instance, &count, physical.data());
    if (status != VK_SUCCESS && status != VK_INCOMPLETE) return devices;
    for (uint32_t i = 0; i < count; ++i) {
        VkPhysicalDeviceProperties props{};
        properties(physical[i], &props);
        if (props.deviceType != VK_PHYSICAL_DEVICE_TYPE_INTEGRATED_GPU &&
            props.deviceType != VK_PHYSICAL_DEVICE_TYPE_DISCRETE_GPU &&
            props.deviceType != VK_PHYSICAL_DEVICE_TYPE_VIRTUAL_GPU) continue;
        uint32_t queue_count = 0;
        queues(physical[i], &queue_count, nullptr);
        std::vector<VkQueueFamilyProperties> families(queue_count);
        queues(physical[i], &queue_count, families.data());
        bool compute = false;
        for (const auto & family : families) {
            if (family.queueCount && (family.queueFlags & VK_QUEUE_COMPUTE_BIT)) compute = true;
        }
        if (!compute) continue;
        OrtHardwareDevice device{};
        device.type = OrtHardwareDeviceType_GPU;
        device.vendor_id = props.vendorID;
        device.device_id = props.deviceID;
        device.vendor = props.deviceName;
        device.metadata.Add("name", props.deviceName);
        device.metadata.Add("discovery", "vulkan");
        devices.emplace(std::move(device));
        LOGS_DEFAULT(INFO) << "Android Vulkan GPU discovered: " << props.deviceName;
    }
    return devices;
}
}
