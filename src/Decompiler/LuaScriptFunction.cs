﻿using System;
using System.Collections.Generic;
using System.Text;
using LuaSharpVM.Core;
using LuaSharpVM.Models;
using LuaSharpVM.Disassembler;
using System.Linq;

namespace LuaSharpVM.Decompiler
{
    public class LuaScriptFunction
    {
        public int Depth;
        private LuaDecoder Decoder;
        private LuaFunction Func;
        private string Name;
        private bool IsLocal = false;
        private List<string> Args;
        public List<LuaScriptLine> Lines;

        public List<LuaScriptBlock> Blocks;

        private string _text;

        public string Text
        {
            get { return GetText(); }
        }

        public LuaScriptFunction(string name, List<string> args, ref LuaFunction func, ref LuaDecoder decoder)
        {
            this.Name = name;
            this.Args = args;
            this.Func = func;
            this.Decoder = decoder;
            this.Lines = new List<LuaScriptLine>();
            this.Blocks = new List<LuaScriptBlock>();
        }

        public override string ToString()
        {
            if (this.Name == null && GetName() != null)
                return "-- root file\n\r";

            string args = "(";
            for (int i = 0; i < this.Args.Count; i++)
            {
                args += this.Args[i];
                if (i < this.Args.Count - 1)
                    args += ", ";
            }
            args += ")";
            return (this.IsLocal ? "local" : "") + $"function {GetName()}{args}\n\r";
        }

        private string GetName()
        {
            if (Func.Name != null && Func.Name != "")
                return Func.Name;
            return this.Name;
        }


        private void GenerateBlocks()
        {
            int index = 0;
            this.Blocks.Clear();
            while (index < this.Lines.Count)
            {
                LuaScriptBlock b = new LuaScriptBlock(index, ref this.Decoder, ref this.Func);
                while (index < this.Lines.Count)
                {
                    if (b.AddScriptLine(this.Lines[index]))
                        break;
                    index++;
                }
                index++;
                this.Blocks.Add(b); // save block
            }

            // add block jumpsFrom and split
            List<KeyValuePair<int, int>> BlockSplitLines = new List<KeyValuePair<int, int>>();// block, line
            for (int i = 0; i < this.Blocks.Count; i++)
            {
                if (this.Blocks[i].JumpsTo == -1)
                    continue;
                var targets = this.Blocks.FindAll(x => x.HasLineNumber(this.Blocks[i].JumpsTo));
                if (targets.Count > 0)
                    BlockSplitLines.Add(new KeyValuePair<int, int>(this.Blocks.FindIndex(x => x.StartAddress == targets[0].StartAddress), this.Blocks[i].JumpsTo));
                //foreach (var tb in targets)
                //    BlockSplitLines.Add(new KeyValuePair<int, int>(this.Blocks.FindIndex(x => x.StartAddress == tb.StartAddress), i));
            }

            BlockSplitLines = BlockSplitLines.OrderBy(x => x.Key).ToList();
            // cut blocks and make new ones
            for (int i = 0; i < BlockSplitLines.Count; i++)
            {
                if (this.Blocks[BlockSplitLines[i].Key].StartAddress + this.Blocks[BlockSplitLines[i].Key].Lines.Count < BlockSplitLines[i].Value ||
                    this.Blocks[BlockSplitLines[i].Key].StartAddress > BlockSplitLines[i].Value)
                    continue; // already circumcised this boi

                if (this.Blocks.Find(x => x.StartAddress == BlockSplitLines[i].Value) != null)
                    continue; // been there, done that

                LuaScriptBlock splitBlock = new LuaScriptBlock(BlockSplitLines[i].Value, ref this.Decoder, ref this.Func);
                for (int j = BlockSplitLines[i].Value - this.Blocks[BlockSplitLines[i].Key].StartAddress; j < this.Blocks[BlockSplitLines[i].Key].Lines.Count; j++)
                    splitBlock.Lines.Add(this.Blocks[BlockSplitLines[i].Key].Lines[j]); // copy from old to new

                // delete old lines
                if (splitBlock.Lines.Count > 0)
                    this.Blocks[BlockSplitLines[i].Key].Lines.RemoveRange(BlockSplitLines[i].Value - this.Blocks[BlockSplitLines[i].Key].StartAddress, splitBlock.Lines.Count);

                this.Blocks.Insert(BlockSplitLines[i].Key + 1, splitBlock); // insert new block after modified one
                // update BlockSplitLines indexing
                for (int j = i + 1; j < BlockSplitLines.Count; j++)
                    BlockSplitLines[j] = new KeyValuePair<int, int>(BlockSplitLines[j].Key + 1, BlockSplitLines[j].Value); // offset
            }
            // fix JumpsTo and JumpsNext ?
            this.Blocks.OrderBy(x => x.StartAddress);
            for (int i = 0; i < this.Blocks.Count; i++)
            {
                // last return
                if (i == this.Blocks.Count - 1)
                {
                    // Last block shouldnt jump to anywhere
                    this.Blocks[i].JumpsTo = -1;
                    this.Blocks[i].JumpsNext = -1;
                    if (this.Blocks[i].Lines[this.Blocks[i].Lines.Count - 1].Instr.OpCode == LuaOpcode.RETURN)
                        this.Blocks[i].Lines[this.Blocks[i].Lines.Count - 1].Op1 = "end"; // replace last RETURN with END
                    continue;
                }
                // Conditions without JMP

                if (this.Blocks[i].Lines.Count > 0)
                    switch (this.Blocks[i].Lines[this.Blocks[i].Lines.Count - 1].Instr.OpCode)
                    {
                        // TODO: check which instructions dont pick the next one
                        case LuaOpcode.TFORLOOP:
                        case LuaOpcode.FORLOOP: // calculate LOOP jump
                            this.Blocks[i].JumpsTo = (this.Blocks[i].StartAddress + this.Blocks[i].Lines.Count - 1) + (short)this.Blocks[i].Lines[this.Blocks[i].Lines.Count - 1].Instr.sBx + 1; // TODO: verify math
                            this.Blocks[i].JumpsNext = this.Blocks[i].JumpsNext = this.Blocks[i + 1].StartAddress;
                            break; // jmp?
                        case LuaOpcode.LOADBOOL: // pc++
                            this.Blocks[i].JumpsTo = (this.Blocks[i].StartAddress + this.Blocks[i].Lines.Count - 1) + 2; // skips one if C
                            this.Blocks[i].JumpsNext = (this.Blocks[i].StartAddress + this.Blocks[i].Lines.Count - 1) + 1; // next block
                            break;
                        case LuaOpcode.JMP: // pc++
                            // check previous condition
                            if (this.Blocks[i].Lines.Count > 1 && this.Blocks[i].Lines[this.Blocks[i].Lines.Count - 2].IsCondition()) // check for IF
                            {
                                // if/test/testset
                                this.Blocks[i].JumpsTo = (this.Blocks[i].StartAddress + this.Blocks[i].Lines.Count - 1) + (short)this.Blocks[i].Lines[this.Blocks[i].Lines.Count - 1].Instr.sBx + 1; // TODO: verify math
                                this.Blocks[i].JumpsNext = this.Blocks[i + 1].StartAddress;
                            }
                            else
                            {
                                // unknown jump
                                this.Blocks[i].JumpsTo = (this.Blocks[i].StartAddress + this.Blocks[i].Lines.Count - 1) + (short)this.Blocks[i].Lines[this.Blocks[i].Lines.Count - 1].Instr.sBx + 1; // TODO: verify math
                                this.Blocks[i].JumpsNext = this.Blocks[i + 1].StartAddress;
                            }
                            break;
                        default:
                            this.Blocks[i].JumpsTo = -1; // erase from possible previous block?
                            this.Blocks[i].JumpsNext = this.Blocks[i + 1].StartAddress;
                            break;
                    }
                else
                {
                    var qwe = 123;
                }
            }

        }

        private void SetUpvalues()
        {
            // iterate instructions and set upvalues
            Dictionary<int, int> UpvalueConstants = new Dictionary<int, int>();
            for (int i = 0; i < this.Lines.Count; i++)
            {
                var instr = this.Lines[i].Instr;
                switch(instr.OpCode)
                {
                    case LuaOpcode.SETUPVAL:
                        if (!UpvalueConstants.ContainsKey(instr.A))
                            UpvalueConstants.Add(instr.A, instr.B); // idk..
                        break;
                    case LuaOpcode.GETUPVAL:
                        //if (instr.B == 0)
                        //    this.Lines[i].Op3 = "self()";
                        //else
                        //    this.Lines[i].Op3 = this.Func.Constants[instr.B].ToString();
                        break;
                }
            }
        }

        public void Complete()
        {
            SetUpvalues();
            GenerateBlocks();
        }

        public string GetText()
        {
            if (_text != null)
                return _text; // stores end results

            string result = this.ToString() + "\n\r";
            result += GenerateCode();
            return result;
        }

        private string GenerateCode()
        {
            string result = "";
            int tabLevel = 0;
            for(int b = 0; b < this.Blocks.Count; b++)
            {
                // print block content
                for(int i = 0; i < this.Blocks[b].Lines.Count; i++)
                    result += (this.Blocks[b].StartAddress + i).ToString("0000") + $": {new string(' ',tabLevel)}" + this.Blocks[b].Lines[i].Text;
                
                result += new string('-', 50) + $" ({this.Blocks[b].JumpsTo}) \n\r\n\r";
                if (b == this.Blocks.Count - 1)
                    result += "\n\r"; // keep it clean?
            }
            return result;
        }

        private string GenerateCodeFlat()
        {
            string result = "";
            int tabLevel = 0;
            for (int b = 0; b < this.Blocks.Count; b++)
            {
                for (int i = 0; i < this.Blocks[b].Lines.Count; i++)
                {
                    result += (this.Blocks[b].StartAddress + i).ToString("0000") + ": " + this.Blocks[b].Lines[i].Text;
                }
                result += new string('-', 50) + $" ({this.Blocks[b].JumpsTo}) \n\r";
                if (b == this.Blocks.Count - 1)
                    result += "\n\r"; // keep it clean?
            }
            return result;
        }
    }

}
