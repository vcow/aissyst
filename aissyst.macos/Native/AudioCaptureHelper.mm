#import <AVFoundation/AVFoundation.h>
#import <AudioToolbox/AudioToolbox.h>
#import <CoreAudio/CoreAudio.h>
#import <CoreAudio/CATapDescription.h>
#import <CoreAudio/AudioHardwareTapping.h>
#include <atomic>
#include <chrono>
#include <csignal>
#include <cstdio>
#include <cstring>
#include <memory>
#include <stdexcept>
#include <string>
#include <thread>

static std::atomic<bool> stopping(false);

static void Check(OSStatus status, const char *operation)
{
    if (status != noErr)
    {
        throw std::runtime_error(std::string(operation) + " failed: OSStatus=" + std::to_string(status));
    }
}

template<class T> static T ReadProperty(AudioObjectID object, AudioObjectPropertySelector selector,
    AudioObjectPropertyScope scope = kAudioObjectPropertyScopeGlobal, UInt32 qualifierSize = 0, const void *qualifier = nullptr)
{
    AudioObjectPropertyAddress address = {selector, scope, kAudioObjectPropertyElementMain};
    T value = {};
    UInt32 size = sizeof(value);
    Check(AudioObjectGetPropertyData(object, &address, qualifierSize, qualifier, &size, &value), "Read audio property");
    return value;
}

// Single-producer/single-consumer queue. Audio callbacks never allocate, wait, or write to a pipe.
class AudioQueue
{
    static constexpr uint32_t Capacity = 128;
    static constexpr uint32_t MaxFrames = 16384;
    static constexpr uint32_t MaxChannels = 32;
    struct Packet
    {
        uint32_t frames, rate, channels;
        uint64_t timestamp;
        float *samples = nullptr;
    };
    std::unique_ptr<Packet[]> packets = std::make_unique<Packet[]>(Capacity);
    std::unique_ptr<float[]> storage;
    uint32_t allocatedChannels = 0;
    std::atomic<uint64_t> written{0}, read{0};
    std::atomic<int> error{0};

public:
    uint32_t rate = 0, channels = 0;

    void Configure(uint32_t sampleRate, uint32_t channelCount)
    {
        if (channelCount == 0 || channelCount > MaxChannels)
        {
            throw std::runtime_error("Unsupported audio channel count.");
        }
        rate = sampleRate;
        channels = allocatedChannels = channelCount;
        size_t stride = (size_t)MaxFrames * channelCount;
        storage = std::unique_ptr<float[]>(new float[stride * Capacity]);
        for (uint32_t i = 0; i < Capacity; i++)
        {
            packets[i].samples = storage.get() + i * stride;
        }
    }

    void Push(const AudioBufferList *buffers, uint32_t sampleRate, uint32_t wantedChannels, uint64_t hostTime)
    {
        if (buffers == nullptr || buffers->mNumberBuffers == 0)
        {
            return;
        }
        uint32_t totalChannels = 0;
        for (UInt32 i = 0; i < buffers->mNumberBuffers; i++)
        {
            totalChannels += buffers->mBuffers[i].mNumberChannels;
        }
        if (wantedChannels == 0 || wantedChannels > allocatedChannels || totalChannels < wantedChannels)
        {
            error.store(2);
            return;
        }

        auto writeIndex = written.load(std::memory_order_relaxed);
        if (writeIndex - read.load(std::memory_order_acquire) >= Capacity)
        {
            error.store(1);
            return;
        }
        auto &packet = packets[writeIndex % Capacity];
        // Physical sub-device inputs precede the appended tap. Never include them in application audio.
        uint32_t skip = totalChannels - wantedChannels;
        uint32_t channelBase = 0, destinationChannel = 0, frames = 0;
        for (UInt32 i = 0; i < buffers->mNumberBuffers; i++)
        {
            const auto &buffer = buffers->mBuffers[i];
            uint32_t sourceChannels = buffer.mNumberChannels;
            if (channelBase + sourceChannels <= skip)
            {
                channelBase += sourceChannels;
                continue;
            }
            if (buffer.mData == nullptr || sourceChannels == 0 || buffer.mDataByteSize == 0)
            {
                return;
            }
            auto bufferFrames = buffer.mDataByteSize / (sizeof(float) * sourceChannels);
            if (bufferFrames > MaxFrames || (frames != 0 && frames != bufferFrames))
            {
                error.store(2);
                return;
            }
            frames = bufferFrames;
            auto samples = static_cast<const float *>(buffer.mData);
            uint32_t firstChannel = skip > channelBase ? skip - channelBase : 0;
            for (uint32_t channel = firstChannel; channel < sourceChannels; channel++)
            {
                for (uint32_t frame = 0; frame < frames; frame++)
                {
                    packet.samples[frame * wantedChannels + destinationChannel] = samples[frame * sourceChannels + channel];
                }
                destinationChannel++;
            }
            channelBase += sourceChannels;
        }
        if (frames == 0)
        {
            return;
        }
        packet.frames = frames;
        packet.rate = sampleRate;
        packet.channels = wantedChannels;
        packet.timestamp = AudioConvertHostTimeToNanos(hostTime);
        written.store(writeIndex + 1, std::memory_order_release);
    }

    bool WriteNext()
    {
        if (error.load() != 0)
        {
            throw std::runtime_error(error.load() == 1 ? "Audio buffer overflow; the consumer is too slow." : "Unsupported audio buffer layout.");
        }
        auto readIndex = read.load(std::memory_order_relaxed);
        if (readIndex == written.load(std::memory_order_acquire))
        {
            return false;
        }
        const auto &packet = packets[readIndex % Capacity];
        const uint32_t header[] = {0x31445541, packet.frames, packet.rate, packet.channels}; // AUD1
        if (fwrite(header, sizeof(header), 1, stdout) != 1
            || fwrite(&packet.timestamp, sizeof(packet.timestamp), 1, stdout) != 1
            || fwrite(packet.samples, sizeof(float), packet.frames * packet.channels, stdout) != packet.frames * packet.channels)
        {
            throw std::runtime_error("The audio consumer closed its output pipe.");
        }
        read.store(readIndex + 1, std::memory_order_release);
        return true;
    }
};

static OSStatus TapCallback(AudioObjectID, const AudioTimeStamp *, const AudioBufferList *input,
    const AudioTimeStamp *inputTime, AudioBufferList *output, const AudioTimeStamp *, void *context)
{
    auto queue = static_cast<AudioQueue *>(context);
    if (output != nullptr)
    {
        for (UInt32 i = 0; i < output->mNumberBuffers; i++)
        {
            if (output->mBuffers[i].mData != nullptr)
            {
                memset(output->mBuffers[i].mData, 0, output->mBuffers[i].mDataByteSize);
            }
        }
    }
    uint64_t time = inputTime != nullptr && (inputTime->mFlags & kAudioTimeStampHostTimeValid)
        ? inputTime->mHostTime : AudioGetCurrentHostTime();
    queue->Push(input, queue->rate, queue->channels, time);
    return noErr;
}

class Capture
{
    AVAudioEngine *engine = nil;
    AudioObjectID tap = kAudioObjectUnknown, aggregate = kAudioObjectUnknown;
    AudioDeviceIOProcID io = nullptr;
    bool microphoneTapInstalled = false;

public:
    ~Capture() { Stop(); }

    void StartMicrophone(NSString *uid, AudioQueue *queue)
    {
        auto status = [AVCaptureDevice authorizationStatusForMediaType:AVMediaTypeAudio];
        if (status == AVAuthorizationStatusNotDetermined)
        {
            auto answered = std::make_shared<std::atomic<bool>>(false);
            [AVCaptureDevice requestAccessForMediaType:AVMediaTypeAudio completionHandler:^(BOOL) { answered->store(true); }];
            while (!answered->load() && !stopping.load())
            {
                CFRunLoopRunInMode(kCFRunLoopDefaultMode, 0.05, true);
                std::this_thread::sleep_for(std::chrono::milliseconds(10));
            }
            status = [AVCaptureDevice authorizationStatusForMediaType:AVMediaTypeAudio];
        }
        if (stopping.load())
        {
            return;
        }
        if (status != AVAuthorizationStatusAuthorized)
        {
            throw std::runtime_error("Microphone access denied. Allow aissyst Audio Capture in System Settings > Privacy & Security > Microphone.");
        }

        CFStringRef deviceUid = (__bridge CFStringRef)uid;
        auto device = ReadProperty<AudioObjectID>(kAudioObjectSystemObject, kAudioHardwarePropertyTranslateUIDToDevice,
            kAudioObjectPropertyScopeGlobal, sizeof(deviceUid), &deviceUid);
        if (device == kAudioObjectUnknown)
        {
            throw std::runtime_error("The selected microphone is no longer available.");
        }

        engine = [[AVAudioEngine alloc] init];
        AVAudioInputNode *input = engine.inputNode;
        Check(AudioUnitSetProperty(input.audioUnit, kAudioOutputUnitProperty_CurrentDevice, kAudioUnitScope_Global, 0, &device, sizeof(device)), "Select microphone");
        AVAudioFormat *format = [input outputFormatForBus:0];
        if (format.commonFormat != AVAudioPCMFormatFloat32 || format.sampleRate <= 0 || format.channelCount == 0)
        {
            throw std::runtime_error("The microphone did not provide a valid Float32 PCM format.");
        }
        queue->Configure((uint32_t)format.sampleRate, format.channelCount);
        [input installTapOnBus:0 bufferSize:1024 format:format block:^(AVAudioPCMBuffer *buffer, AVAudioTime *time) {
            queue->Push(buffer.audioBufferList, (uint32_t)buffer.format.sampleRate, buffer.format.channelCount,
                time.isHostTimeValid ? time.hostTime : AudioGetCurrentHostTime());
        }];
        microphoneTapInstalled = true;
        NSError *error = nil;
        if (![engine startAndReturnError:&error])
        {
            throw std::runtime_error(std::string("Start microphone failed: ") + error.localizedDescription.UTF8String);
        }
    }

    void StartApplication(NSString *pidList, AudioQueue *queue, bool system)
    {
        NSMutableArray<NSNumber *> *processes = [NSMutableArray array];
        if (!system)
        {
            for (NSString *text in [pidList componentsSeparatedByString:@","])
            {
                pid_t pid = text.intValue;
                auto process = ReadProperty<AudioObjectID>(kAudioObjectSystemObject,
                    kAudioHardwarePropertyTranslatePIDToProcessObject, kAudioObjectPropertyScopeGlobal, sizeof(pid), &pid);
                if (process != kAudioObjectUnknown)
                {
                    [processes addObject:@(process)];
                }
            }
            if (processes.count == 0)
            {
                throw std::runtime_error("The selected application no longer has an audio process. Refresh the application list.");
            }
        }
        CATapDescription *description = system
            ? [[CATapDescription alloc] initStereoGlobalTapButExcludeProcesses:@[]]
            : [[CATapDescription alloc] initStereoMixdownOfProcesses:processes];
        description.UUID = [NSUUID UUID];
        [description setPrivate:YES];
        description.muteBehavior = CATapUnmuted;
        Check(AudioHardwareCreateProcessTap(description, &tap), "Create application audio tap");
        auto format = ReadProperty<AudioStreamBasicDescription>(tap, kAudioTapPropertyFormat);
        if (format.mFormatID != kAudioFormatLinearPCM || !(format.mFormatFlags & kAudioFormatFlagIsFloat) || format.mBitsPerChannel != 32)
        {
            throw std::runtime_error("The application audio tap did not provide Float32 PCM.");
        }

        auto output = ReadProperty<AudioObjectID>(kAudioObjectSystemObject, kAudioHardwarePropertyDefaultOutputDevice);
        NSString *outputUid = CFBridgingRelease(ReadProperty<CFStringRef>(output, kAudioDevicePropertyDeviceUID));
        NSDictionary *composition = @{
            @kAudioAggregateDeviceNameKey: @"aissyst Application Audio",
            @kAudioAggregateDeviceUIDKey: [NSUUID UUID].UUIDString,
            @kAudioAggregateDeviceIsPrivateKey: @YES,
            @kAudioAggregateDeviceIsStackedKey: @NO,
            @kAudioAggregateDeviceMainSubDeviceKey: outputUid,
            @kAudioAggregateDeviceSubDeviceListKey: @[@{@kAudioSubDeviceUIDKey: outputUid, @kAudioSubDeviceInputChannelsKey: @0}],
            @kAudioAggregateDeviceTapAutoStartKey: @YES,
            @kAudioAggregateDeviceTapListKey: @[@{@kAudioSubTapUIDKey: description.UUID.UUIDString, @kAudioSubTapDriftCompensationKey: @YES}]
        };
        Check(AudioHardwareCreateAggregateDevice((__bridge CFDictionaryRef)composition, &aggregate), "Create private audio aggregate device");
        queue->Configure((uint32_t)ReadProperty<Float64>(aggregate, kAudioDevicePropertyNominalSampleRate), format.mChannelsPerFrame);
        Check(AudioDeviceCreateIOProcID(aggregate, TapCallback, queue, &io), "Install application audio callback");
        Check(AudioDeviceStart(aggregate, io), "Start application audio capture");
    }

    void Stop()
    {
        if (engine != nil)
        {
            [engine stop];
            if (microphoneTapInstalled)
            {
                [engine.inputNode removeTapOnBus:0];
                microphoneTapInstalled = false;
            }
            engine = nil;
        }
        if (aggregate != kAudioObjectUnknown)
        {
            if (io != nullptr)
            {
                AudioDeviceStop(aggregate, io);
                AudioDeviceDestroyIOProcID(aggregate, io);
                io = nullptr;
            }
            AudioHardwareDestroyAggregateDevice(aggregate);
            aggregate = kAudioObjectUnknown;
        }
        if (tap != kAudioObjectUnknown)
        {
            AudioHardwareDestroyProcessTap(tap);
            tap = kAudioObjectUnknown;
        }
    }
};

int main(int argc, const char *argv[])
{
    @autoreleasepool
    {
        if (argc < 2 || (strcmp(argv[1], "microphone") != 0 && strcmp(argv[1], "application") != 0 && strcmp(argv[1], "system") != 0))
        {
            fprintf(stderr, "Usage: aissyst-audio-helper microphone <UID> | application <PID,...> | system\n");
            return 2;
        }
        if (argc < 3 && strcmp(argv[1], "system") != 0)
        {
            fprintf(stderr, "A microphone UID or application process list is required.\n");
            return 2;
        }
        signal(SIGPIPE, SIG_IGN);
        signal(SIGINT, [](int) { stopping.store(true); });
        signal(SIGTERM, [](int) { stopping.store(true); });
        setvbuf(stdout, nullptr, _IONBF, 0);
        std::thread([] {
            while (getchar() != EOF)
            {
                stopping.store(true);
                return;
            }
            stopping.store(true);
        }).detach();

        @try
        {
            try
            {
                AudioQueue queue;
                Capture capture;
                if (strcmp(argv[1], "microphone") == 0)
                {
                    capture.StartMicrophone([NSString stringWithUTF8String:argv[2]], &queue);
                }
                else
                {
                    capture.StartApplication(argc >= 3 ? [NSString stringWithUTF8String:argv[2]] : @"", &queue, strcmp(argv[1], "system") == 0);
                }
                while (!stopping.load())
                {
                    if (!queue.WriteNext())
                    {
                        std::this_thread::sleep_for(std::chrono::milliseconds(2));
                    }
                }
                capture.Stop();
                while (queue.WriteNext())
                {
                }
                return 0;
            }
            catch (const std::exception &error)
            {
                fprintf(stderr, "%s\n", error.what());
                return 1;
            }
        }
        @catch (NSException *exception)
        {
            fprintf(stderr, "Audio capture failed: %s\n", exception.reason.UTF8String);
            return 1;
        }
    }
}
