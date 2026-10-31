// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#include <Windows.h>
#include <array>
#include <cstdio>
#include <fcntl.h>
#include <io.h>
#include <memory>
#include <share.h>
#include <stdexcept>
#include <sys/stat.h>
#include <tuple>
#include <zlib.h>
#include <libxml/parser.h>

#if defined(LIBXML_HTML_ENABLED) || defined(LIBXML_XINCLUDE_ENABLED)
#error Unused HTML and XInclude paths must not be compiled into the qualified dependency.
#endif

namespace OpenDot::Sol::Dependencies::Test
{
struct TemporaryFile final
{
    std::array<wchar_t, MAX_PATH + 1> Path{};

    TemporaryFile()
    {
        std::array<wchar_t, MAX_PATH + 1> directory{};
        const auto length = GetTempPathW(static_cast<DWORD>(directory.size()), directory.data());
        if (!length || length >= directory.size() || !GetTempFileNameW(directory.data(), L"sol", 0, Path.data()))
        {
            throw std::runtime_error("Cannot create dependency test file.");
        }
    }

    TemporaryFile(const TemporaryFile &) = delete;
    TemporaryFile &operator=(const TemporaryFile &) = delete;
    TemporaryFile(TemporaryFile &&) = delete;
    TemporaryFile &operator=(TemporaryFile &&) = delete;

    ~TemporaryFile()
    {
        std::ignore = DeleteFileW(Path.data());
    }
};

struct GzipCloser final
{
    void operator()(gzFile file) const noexcept
    {
        std::ignore = gzclose(file);
    }
};

int GzipWriteFailure()
{
    const TemporaryFile temporary;
    int descriptor = -1;
    if (_wsopen_s(&descriptor, temporary.Path.data(), _O_RDONLY | _O_BINARY, _SH_DENYNO, _S_IREAD))
    {
        return 1;
    }
    std::unique_ptr<gzFile_s, GzipCloser> file{gzdopen(descriptor, "wb")};
    if (!file)
    {
        std::ignore = _close(descriptor);
        return 2;
    }
    std::array<unsigned char, 32768> input{};
    if (gzbuffer(file.get(), 128) != 0 || gzsetparams(file.get(), Z_NO_COMPRESSION, Z_DEFAULT_STRATEGY) != Z_OK)
    {
        return 3;
    }
    // The read-only descriptor forces the underlying write to fail after consuming caller input.
    if (gzwrite(file.get(), input.data(), static_cast<unsigned>(input.size())) != 0)
    {
        return 4;
    }
    int error = Z_OK;
    std::ignore = gzerror(file.get(), &error);
    if (error != Z_ERRNO)
    {
        return 5;
    }
    gzclearerr(file.get());
    // A stale next_in pointer used to corrupt the gzip input buffer on this transition.
    if (gzprintf(file.get(), "%s", "after-error") != 11)
    {
        return 6;
    }
    return 0;
}

int XmlAndCompression()
{
    constexpr char xml[] = "<assembly><identity name=\"Sol\"/></assembly>";
    std::unique_ptr<xmlDoc, decltype(&xmlFreeDoc)> document{
        xmlReadMemory(xml, static_cast<int>(sizeof(xml) - 1), nullptr, nullptr, XML_PARSE_NONET | XML_PARSE_NO_XXE),
        &xmlFreeDoc};
    if (!document || !xmlDocGetRootElement(document.get()))
    {
        return 7;
    }
    std::array<unsigned char, 1024> input{};
    std::array<unsigned char, 2048> compressed{};
    std::array<unsigned char, 1024> restored{};
    uLongf size = static_cast<uLongf>(compressed.size());
    if (compress2(compressed.data(), &size, input.data(), static_cast<uLong>(input.size()), Z_BEST_SPEED) != Z_OK)
    {
        return 8;
    }
    uLongf output = static_cast<uLongf>(restored.size());
    if (uncompress(restored.data(), &output, compressed.data(), size) != Z_OK || output != input.size() || restored !=
        input)
    {
        return 9;
    }
    return 0;
}
}

int main()
{
    std::ignore = SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX);
    using namespace OpenDot::Sol::Dependencies::Test;
    if (const auto code = GzipWriteFailure())
    {
        return code;
    }
    if (const auto code = XmlAndCompression())
    {
        return code;
    }
    std::puts("Native dependency smoke passed.");
    return 0;
}
