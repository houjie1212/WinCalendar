function PathAttributes(Name: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';

// 检查每一级父目录，拒绝沿目录链接进入其他位置。
function PlainPath(Name: String): Boolean;
var
  Parent: String;
  Attr: Cardinal;
begin
  Result := False;
  while Name <> '' do begin
    Attr := PathAttributes(Name);
    if (Attr <> $FFFFFFFF) and ((Attr and $400) <> 0) then Exit;
    Parent := ExtractFileDir(Name);
    if CompareText(Parent, Name) = 0 then Break;
    Name := Parent;
  end;
  Result := True;
end;

// 不使用递归 DelTree，逐项检查链接并保留无法安全删除的条目。
function DeleteUserTree(Name: String): Boolean;
var
  FindRec: TFindRec;
  Child: String;
begin
  Result := False;
  if not PlainPath(Name) then Exit;
  if not DirExists(Name) then begin Result := True; Exit; end;
  Result := True;
  if FindFirst(AddBackslash(Name) + '*', FindRec) then begin
    try
      repeat
        if (FindRec.Name <> '.') and (FindRec.Name <> '..') then begin
          Child := AddBackslash(Name) + FindRec.Name;
          if (FindRec.Attributes and $400) <> 0 then Result := False
          else if (FindRec.Attributes and $10) <> 0 then begin
            if not DeleteUserTree(Child) then Result := False;
          end else if not DeleteFile(Child) then Result := False;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
  if not RemoveDir(Name) then Result := False;
end;

