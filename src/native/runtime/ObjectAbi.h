// SPDX-FileCopyrightText: 2026 OpenDot Project <opendotproject.opendot@gmail.com>
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
#pragma once
#include "SolRT.h"
#include <cstddef>

namespace OpenDot::Sol::Runtime
{
/// The table is metadata, not a managed reference. A null interface has both words null.
struct InterfaceValue final
{
    Header *Object;
    const void *Table;
};

/// Immutable once published; a heap interface slot atomically stores a reference to this cell.
struct InterfaceCell final
{
    Header Object;
    Header *Value;
    const void *Table;
};

/// Scoped location transport. Owner is rooted; CallerRoot points to an existing caller root slot.
struct ReferenceLocation final
{
    std::uint32_t Kind;
    std::uint32_t Flags;
    Header *Owner;
    std::uint64_t AddressOrOffset;
    Header **CallerRoot;
};

static_assert(sizeof(InterfaceValue) == 16 && alignof(InterfaceValue) == 8);
static_assert(offsetof(InterfaceValue, Object) == 0 && offsetof(InterfaceValue, Table) == 8);
static_assert(sizeof(InterfaceCell) == 32 && alignof(InterfaceCell) == 8);
static_assert(offsetof(InterfaceCell, Value) == 16 && offsetof(InterfaceCell, Table) == 24);
static_assert(sizeof(ReferenceLocation) == 32 && alignof(ReferenceLocation) == 8);
static_assert(offsetof(ReferenceLocation, Kind) == 0 && offsetof(ReferenceLocation, Flags) == 4);
static_assert(offsetof(ReferenceLocation, Owner) == 8 && offsetof(ReferenceLocation, AddressOrOffset) == 16);
static_assert(offsetof(ReferenceLocation, CallerRoot) == 24);
}
