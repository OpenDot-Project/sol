// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception
// Implemented integral overloads; floating and extended APIs are tracked separately.
module std.math

func abs(value: sbyte): sbyte { val zero: sbyte = 0; if value < zero { return -value }; value }
func min(left, right: sbyte): sbyte { if left < right { return left }; right }
func max(left, right: sbyte): sbyte { if left > right { return left }; right }

func abs(value: short): short { val zero: short = 0; if value < zero { return -value }; value }
func min(left, right: short): short { if left < right { return left }; right }
func max(left, right: short): short { if left > right { return left }; right }

func abs(value: int): int { val zero: int = 0; if value < zero { return -value }; value }
func min(left, right: int): int { if left < right { return left }; right }
func max(left, right: int): int { if left > right { return left }; right }

func abs(value: long): long { val zero: long = 0; if value < zero { return -value }; value }
func min(left, right: long): long { if left < right { return left }; right }
func max(left, right: long): long { if left > right { return left }; right }

func abs(value: byte): byte = value
func min(left, right: byte): byte { if left < right { return left }; right }
func max(left, right: byte): byte { if left > right { return left }; right }

func abs(value: uint): uint = value
func min(left, right: uint): uint { if left < right { return left }; right }
func max(left, right: uint): uint { if left > right { return left }; right }

func abs(value: ulong): ulong = value
func min(left, right: ulong): ulong { if left < right { return left }; right }
func max(left, right: ulong): ulong { if left > right { return left }; right }
