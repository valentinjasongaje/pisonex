from fastapi import APIRouter, Depends
from sqlalchemy.orm import Session

from database import get_db
from dependencies import verify_client_key
from schemas import (
    MemberRegisterRequest, MemberRegisterResponse,
    MemberLoginRequest, MemberLoginResponse,
    MemberLogoutRequest, MemberLogoutResponse,
    MemberStatusResponse,
)
from services.membership_service import MembershipService

router = APIRouter(prefix="/api/member", tags=["member"])

_ClientAuth = Depends(verify_client_key)


@router.post("/register", response_model=MemberRegisterResponse, dependencies=[_ClientAuth])
def register_member(req: MemberRegisterRequest, db: Session = Depends(get_db)):
    svc = MembershipService(db)
    result = svc.register_member(req.username, req.password, req.pc_number)
    return MemberRegisterResponse(**result)


@router.post("/login", response_model=MemberLoginResponse, dependencies=[_ClientAuth])
def login_member(req: MemberLoginRequest, db: Session = Depends(get_db)):
    svc = MembershipService(db)
    result = svc.login_member(req.pc_number, req.username, req.password)
    return MemberLoginResponse(**result)


@router.post("/logout", response_model=MemberLogoutResponse, dependencies=[_ClientAuth])
def logout_member(req: MemberLogoutRequest, db: Session = Depends(get_db)):
    svc = MembershipService(db)
    result = svc.logout_member(req.pc_number)
    return MemberLogoutResponse(**result)


@router.get("/status/{pc_number}", response_model=MemberStatusResponse, dependencies=[_ClientAuth])
def member_status(pc_number: int, db: Session = Depends(get_db)):
    svc = MembershipService(db)
    result = svc.get_member_status(pc_number)
    return MemberStatusResponse(**result)
